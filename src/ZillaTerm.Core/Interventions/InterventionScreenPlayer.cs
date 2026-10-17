using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZillaTerm.Core.Interventions;

/// <summary>
/// Écrans enregistrés dans le rapport d'intervention : un lecteur par session Bureau à distance, qui recompose l'écran à
/// partir des images en tuiles (<see cref="ScreenTileEncoder"/>), avec sous le curseur de position un graphique de
/// l'activité (part de l'écran modifiée) : un clic sur un pic y mène. Les pauses de l'affichage (le serveur n'envoyait
/// plus l'image) sont des bandes grises sur la frise, et l'écran figé y porte « pause ».
/// </summary>
/// <remarks>
/// Le seul script de la page est <see cref="Script"/>, écrit ici et autorisé par son empreinte SHA-256 dans la politique
/// de sécurité de la page : aucun autre script ne s'exécute. Les images voyagent en base64 dans des blocs de données (non
/// exécutés) ; le texte (nom de la session) est échappé ou posé comme texte. Les images sont décompressées par le
/// décodeur Brotli de référence embarqué dans le script (format 3) ou par le navigateur (format 2, DecompressionStream).
/// </remarks>
internal static class InterventionScreenPlayer
{
    /// <summary>Un enregistrement d'écran : une session, ses images dans l'ordre, et les événements de son journal.</summary>
    internal sealed record Recording(int Stream, string Source, List<InterventionEntry> Frames, IReadOnlyList<InterventionEntry> Journal);

    /// <summary>Images d'écran du journal, par session, dans l'ordre de leur premier passage.</summary>
    internal static List<Recording> Recordings(IReadOnlyList<InterventionEntry> entries)
    {
        var byStream = new Dictionary<int, Recording>();
        var order = new List<Recording>();
        foreach (var entry in entries)
        {
            if (entry.Kind != InterventionKind.Screen || entry.Image is not { } image || !ScreenFrameHeader.TryRead(image, out var header))
            {
                continue;
            }

            if (!byStream.TryGetValue(header.Stream, out var recording))
            {
                recording = new Recording(header.Stream, entry.Source, [], entries);
                byStream[header.Stream] = recording;
                order.Add(recording);
            }

            recording.Frames.Add(entry);
        }

        // Une session dont aucune image clé n'a été gardée ne peut pas être rejouée.
        order.RemoveAll(r => !r.Frames.Any(f => ScreenFrameHeader.TryRead(f.Image, out var h) && h.Key));
        return order;
    }

    /// <summary>Empreinte du script pour la politique de sécurité de la page (« 'sha256-…' »).</summary>
    internal static string ScriptHash => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Script))) + "'";

    /// <summary>
    /// Section des écrans enregistrés : un lecteur par session, avec sous le curseur l'activité et les notes prises pendant
    /// la session (repères qui y mènent), puis les textes du lecteur et le script.
    /// </summary>
    /// <param name="notes">Notes de l'intervention, modifications appliquées.</param>
    internal static void Append(StringBuilder html, List<Recording> recordings, IReadOnlyList<InterventionEntry> notes, InterventionReportText text, Func<DateTime, string> local, TimeZoneInfo zone, CultureInfo culture)
    {
        html.Append("<h2>").Append(Encode(text.Screens)).Append("</h2>\n<p class=\"help\">").Append(Encode(text.ScreenHelp)).Append("</p>\n");
        var prefix = new byte[8];
        foreach (var recording in recordings)
        {
            var first = recording.Frames[0].Time;
            // La fin de chaque session (fermeture…) dans son journal prolonge sa ligne de temps.
            var last = End(recording, recording.Journal);
            long bytes = recording.Frames.Sum(f => (long)f.Image!.Length);
            var startLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(first, DateTimeKind.Utc), zone);
            ScreenFrameHeader.TryRead(recording.Frames.First(f => ScreenFrameHeader.TryRead(f.Image, out var h) && h.Key).Image, out var key);

            // Images à la suite : décalage depuis la première (ms, arrondi à la seconde : le curseur avance par seconde et
            // une image prise à 3,012 s doit se voir à 3 s), longueur, image.
            using var container = new MemoryStream();
            foreach (var frame in recording.Frames)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)Math.Max(0, Math.Floor((frame.Time - first).TotalSeconds) * 1000));
                BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), (uint)frame.Image!.Length);
                container.Write(prefix);
                container.Write(frame.Image);
            }

            long duration = ((long)Math.Floor((last - first).TotalSeconds) * 1000) + 1000;
            html.Append("<section class=\"screen\" data-start=\"")
                .Append(((int)startLocal.TimeOfDay.TotalSeconds).ToString(CultureInfo.InvariantCulture)).Append("\" data-duration=\"")
                .Append(duration.ToString(CultureInfo.InvariantCulture)).Append("\">\n<h3>")
                .Append(Encode(string.Format(culture, text.ScreenHeading, recording.Source, local(first), local(last),
                    Ssh.RemotePath.FormatSize(bytes))))
                .Append("</h3>\n<div class=\"stage\"><canvas class=\"screen-canvas\" width=\"")
                .Append(key.Width.ToString(CultureInfo.InvariantCulture)).Append("\" height=\"")
                .Append(key.Height.ToString(CultureInfo.InvariantCulture)).Append("\"></canvas></div>\n")
                .Append("<p class=\"note warn gap\" hidden>").Append(Encode(text.ScreenGap)).Append("</p>\n")
                .Append("<div class=\"controls\"><button type=\"button\" class=\"play\">").Append(Encode(text.Play))
                .Append("</button> <label>").Append(Encode(text.Speed))
                .Append(" <select class=\"speed\"><option value=\"1\">1×</option><option value=\"4\">4×</option>")
                .Append("<option value=\"16\" selected>16×</option><option value=\"60\">60×</option></select></label> ")
                .Append("<span class=\"clock\"></span></div>\n")
                .Append("<div class=\"track\"><canvas class=\"activity\" height=\"40\"></canvas>")
                .Append("<input type=\"range\" class=\"seek\" min=\"0\" max=\"0\" step=\"1000\" value=\"0\" aria-label=\"")
                .Append(Encode(text.Activity)).Append("\" title=\"").Append(Encode(text.Activity)).Append("\"></div>\n");
            AppendMarks(html, notes.Where(n => n.Time >= first && n.Time <= last), Pauses(recording, last), first, duration, text, zone, culture);
            html.Append("<noscript><p class=\"note warn\">").Append(Encode(text.ScriptNeeded)).Append("</p></noscript>\n")
                .Append("<script type=\"application/octet-stream\" class=\"frames\">")
                .Append(Convert.ToBase64String(container.GetBuffer(), 0, (int)container.Length))
                .Append("</script>\n</section>\n");
        }

        // Textes du lecteur : JSON dont « < », « > » et « & » sont échappés, il ne peut pas fermer son bloc.
        html.Append("<script type=\"application/json\" id=\"zt-player\">")
            .Append(JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["play"] = text.Play,
                ["pause"] = text.Pause,
                ["paused"] = text.Paused,
                ["idle"] = text.PausedIdle,
                ["hidden"] = text.PausedHidden,
            }))
            .Append("</script>\n<script>").Append(Script).Append("</script>\n");
    }

    /// <summary>
    /// Fin de la ligne de temps d'une session : sa dernière trace après sa dernière image (fermeture de l'onglet,
    /// capture…), sinon la fin de l'enregistrement. Un écran immobile n'envoie plus d'image : sa dernière image vaut
    /// jusque-là, et une note prise en le regardant tombe dans sa ligne de temps.
    /// </summary>
    private static DateTime End(Recording recording, IReadOnlyList<InterventionEntry> all)
    {
        var last = recording.Frames[^1].Time;
        var traces = all.Where(e => e.Kind != InterventionKind.Screen && e.Time > last).ToList();
        var session = traces.Where(e => e.Source == recording.Source).Select(e => e.Time).DefaultIfEmpty(last).Max();
        return session > last ? session : traces.Select(e => e.Time).DefaultIfEmpty(last).Max();
    }

    /// <summary>
    /// Pauses de l'affichage de la session : de chaque image de pause à l'image suivante (ou à la fin de sa ligne de
    /// temps), avec leur raison.
    /// </summary>
    internal static List<(DateTime From, DateTime To, ScreenPause Reason)> Pauses(Recording recording, DateTime end)
    {
        var pauses = new List<(DateTime, DateTime, ScreenPause)>();
        var frames = recording.Frames;
        for (int i = 0; i < frames.Count; i++)
        {
            if (ScreenFrameHeader.TryRead(frames[i].Image, out var header) && header.Pause != ScreenPause.None)
            {
                pauses.Add((frames[i].Time, i + 1 < frames.Count ? frames[i + 1].Time : end, header.Pause));
            }
        }

        return pauses;
    }

    /// <summary>
    /// Repères sous le curseur : les pauses de l'affichage (bandes grises, de leur début à leur fin) et les notes, à leur
    /// heure. Un clic y mène, le texte s'affiche au survol et est lu par les lecteurs d'écran. Le texte est échappé
    /// (attribut).
    /// </summary>
    private static void AppendMarks(StringBuilder html, IEnumerable<InterventionEntry> notes,
        List<(DateTime From, DateTime To, ScreenPause Reason)> pauses, DateTime first, long duration,
        InterventionReportText text, TimeZoneInfo zone, CultureInfo culture)
    {
        var marks = notes.ToList();
        if (marks.Count == 0 && pauses.Count == 0)
        {
            return;
        }

        string Time(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("T", culture);
        long Ms(DateTime utc) => (long)Math.Floor((utc - first).TotalSeconds) * 1000;
        string Percent(double ms) => (ms * 100.0 / duration).ToString("0.###", CultureInfo.InvariantCulture);
        html.Append("<div class=\"marks\">");
        foreach (var (from, to, reason) in pauses)
        {
            long start = Ms(from);
            var label = Encode(string.Format(culture, text.PauseMarker, Time(from), Time(to),
                reason == ScreenPause.Idle ? text.PausedIdle : text.PausedHidden));
            html.Append("<button type=\"button\" class=\"mark pause\" data-ms=\"").Append(start.ToString(CultureInfo.InvariantCulture))
                .Append("\" style=\"left:").Append(Percent(start)).Append("%;width:").Append(Percent(Math.Max(Ms(to) - start, 1000)))
                .Append("%\" title=\"").Append(label).Append("\" aria-label=\"").Append(label).Append("\"></button>");
        }

        foreach (var note in marks)
        {
            long ms = Ms(note.Time);
            var label = Encode(string.Format(culture, text.NoteMarker, Time(note.Time), note.Text));
            html.Append("<button type=\"button\" class=\"mark\" data-ms=\"").Append(ms.ToString(CultureInfo.InvariantCulture))
                .Append("\" style=\"left:").Append(Percent(ms))
                .Append("%\" title=\"").Append(label).Append("\" aria-label=\"").Append(label).Append("\"></button>");
        }

        html.Append("</div>\n");
    }

    /// <summary>Styles du lecteur : le curseur de position est un trait posé sur le graphique d'activité.</summary>
    internal const string Styles =
        "section.screen{margin:12px 0 28px}.stage{border:1px solid #d0d7de;background:#0c0c0c;text-align:center}"
        + ".stage canvas{display:inline-block;vertical-align:top;max-width:100%;max-height:70vh;width:auto;height:auto}"
        + ".controls{margin:8px 0 4px;display:flex;gap:12px;align-items:center}.clock{font-variant-numeric:tabular-nums;color:#57606a}"
        + ".track{position:relative;height:42px}.track canvas{position:absolute;left:0;top:0;width:100%;height:40px;background:#f3f5f7;"
        + "border:1px solid #d0d7de;box-sizing:border-box}"
        + ".track input{position:absolute;left:0;top:0;width:100%;height:40px;margin:0;background:transparent;-webkit-appearance:none;appearance:none;cursor:pointer}"
        + ".track input::-webkit-slider-runnable-track{height:40px;background:transparent}"
        + ".track input::-webkit-slider-thumb{-webkit-appearance:none;width:3px;height:40px;background:#c62828}"
        + ".track input::-moz-range-track{height:40px;background:transparent}"
        + ".track input::-moz-range-thumb{width:3px;height:40px;background:#c62828;border:0;border-radius:0}"
        + ".track input:focus-visible{outline:2px solid #1565c0;outline-offset:2px}.help{color:#57606a}"
        + ".marks{position:relative;height:16px;margin-top:2px}.mark{position:absolute;top:0;width:12px;height:14px;margin-left:-6px;"
        + "padding:0;border:0;background:#e65100;cursor:pointer;clip-path:polygon(50% 0,100% 100%,0 100%)}"
        + ".mark:hover,.mark:focus-visible{background:#1565c0;outline:none}"
        + ".mark.pause{margin-left:0;min-width:3px;height:10px;top:2px;background:#8c959f;clip-path:none}"
        + ".mark.pause:hover,.mark.pause:focus-visible{background:#57606a;outline:2px solid #1565c0}";

    /// <summary>
    /// Lecteur : décode le conteneur d'images d'une session, rejoue depuis l'image clé précédente pour aller à une heure,
    /// lit à la vitesse choisie, et dessine sous le curseur l'activité (part de l'écran modifiée, en millièmes, par image).
    /// Une image manquante ou illisible fige l'écran jusqu'à l'image clé suivante, avec un avis. Un clic sur le repère
    /// d'une note mène à son heure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Images au format 3 (<see cref="ScreenTileEncoder"/>) : le flux Brotli d'une image clé à la suivante est décompressé
    /// d'un coup (décodeur de référence de Brotli, Google, licence MIT, embarqué : Interventions/Player/brotli-decode.js,
    /// les navigateurs ne décompressant que le deflate), puis découpé image par image. Images au format 2 (journaux
    /// d'avant la 0.42) : zlib image par image, décompressé par le navigateur (DecompressionStream).
    /// </para>
    /// <para>
    /// Fins de ligne ramenées à LF : le navigateur fait de même avant de calculer l'empreinte d'un script de la page
    /// (<see cref="ScriptHash"/>). Compilé sous Windows, le texte du fichier source est en CRLF : sans cela, l'empreinte
    /// ne correspondait plus et le lecteur était bloqué par la politique de sécurité du rapport.
    /// </para>
    /// </remarks>
    internal static readonly string Script = ("(() => {\n" + BrotliDecoder() + "\n" + PlayerSource + "\n})();\n").ReplaceLineEndings("\n");

    /// <summary>Décodeur Brotli embarqué, dans la portée du lecteur (pas de module : une seule balise script autorisée).</summary>
    private static string BrotliDecoder()
    {
        using var stream = typeof(InterventionScreenPlayer).Assembly.GetManifestResourceStream("ZillaTerm.Core.BrotliDecode.js")
            ?? throw new InvalidOperationException("Brotli decoder missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var source = reader.ReadToEnd();
        const string export = "export let BrotliDecode = makeBrotliDecode();";
        return source.Contains(export, StringComparison.Ordinal)
            ? source.Replace(export, "const BrotliDecode = makeBrotliDecode();", StringComparison.Ordinal)
            : throw new InvalidOperationException("Unexpected Brotli decoder.");
    }

    private const string PlayerSource = """
  const words = JSON.parse(document.getElementById('zt-player').textContent);
  const inflate = async bytes => new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(new DecompressionStream('deflate'))).arrayBuffer());
  const two = n => String(n).padStart(2, '0');
  const clockOf = s => { s = Math.floor(s) % 86400; return two(Math.floor(s / 3600)) + ':' + two(Math.floor(s / 60) % 60) + ':' + two(s % 60); };
  // Pixels : 0x00RRGGBB ; RGB565 élargi comme le fait FreeRDP.
  const to565 = v => (((v >> 19) & 31) << 11) | (((v >> 10) & 63) << 5) | ((v >> 3) & 31);
  const from565 = v => { const r = v >> 11, g = (v >> 5) & 63, b = v & 31; return (((r << 3) | (r >> 2)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 3) | (b >> 2)); };
  const rgba = v => (0xFF000000 | ((v & 0xFF) << 16) | (v & 0xFF00) | ((v >> 16) & 0xFF)) >>> 0;
  for (const section of document.querySelectorAll('section.screen')) {
    const raw = atob(section.querySelector('script.frames').textContent.trim());
    const data = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) data[i] = raw.charCodeAt(i);
    const view = new DataView(data.buffer);
    const frames = [];
    for (let o = 0; o + 8 <= data.length;) {
      const t = view.getUint32(o, true), n = view.getUint32(o + 4, true);
      o += 8;
      if (o + n > data.length) break;
      const p = data.subarray(o, o + n);
      o += n;
      // « ZTS2 » (en-tête de 32 octets) ou « ZTS3 » (36 octets : copies d'écran, couleurs en 24 bits).
      if (p.length < 32 || p[0] !== 90 || p[1] !== 84 || p[2] !== 83 || (p[3] !== 50 && p[3] !== 51)) continue;
      const fmt = p[3] - 48, hlen = fmt === 3 ? 36 : 32;
      if (p.length < hlen) continue;
      const h = new DataView(p.buffer, p.byteOffset, p.byteLength);
      const f = { t, p, fmt, hlen, seq: h.getUint32(8, true), w: h.getUint16(12, true), h: h.getUint16(14, true), key: (p[16] & 1) === 1,
        pause: (p[16] >> 1) & 3, tc: fmt === 3 && (p[16] & 8) !== 0, size: p[17], activity: h.getUint16(18, true), first: h.getUint32(20, true),
        count: h.getUint32(24, true), places: h.getUint32(28, true), moves: fmt === 3 ? h.getUint32(32, true) : 0 };
      f.length = f.moves * 12 + f.places * 8 + f.count * 4 + f.count * f.size * f.size * (f.tc ? 3 : 2);
      frames.push(f);
    }
    if (!frames.length) continue;
    const canvas = section.querySelector('canvas.screen-canvas'), ctx = canvas.getContext('2d');
    const seek = section.querySelector('input.seek'), clock = section.querySelector('.clock');
    const play = section.querySelector('button.play'), speed = section.querySelector('select.speed');
    const graph = section.querySelector('canvas.activity'), g = graph.getContext('2d'), gap = section.querySelector('.gap');
    const start = Number(section.dataset.start) || 0;
    const duration = Math.max(frames[frames.length - 1].t + 1000, Number(section.dataset.duration) || 0);
    seek.max = String(duration);
    // Pauses de l'affichage : d'une image de pause à l'image suivante (ou à la fin).
    const pauses = [];
    frames.forEach((f, i) => { if (f.pause) pauses.push({ from: f.t, to: i + 1 < frames.length ? frames[i + 1].t : duration, kind: f.pause }); });
    let tiles = new Map(), image = null, pixels = null, width = 0, height = 0, current = -1, wanted = -1, running = false;
    let playing = false, position = 0, last = 0;

    // Format 3 : contenus d'un intervalle entre images clés, décompressés d'un coup (le flux Brotli part de l'image clé) ;
    // un morceau illisible ou une image manquante arrête l'intervalle là.
    let interval = { key: -1, bodies: [] };
    const decodeInterval = k => {
      const list = [];
      for (let i = k; i < frames.length && (i === k || !frames[i].key); i++) {
        if (frames[i].fmt !== 3 || frames[i].seq !== frames[k].seq + (i - k)) break;
        list.push(frames[i]);
      }
      const attempt = n => {
        let total = 1;
        for (let j = 0; j < n; j++) total += list[j].p.length - list[j].hlen;
        const input = new Int8Array(total);
        let at = 0;
        for (let j = 0; j < n; j++) { input.set(list[j].p.subarray(list[j].hlen), at); at += list[j].p.length - list[j].hlen; }
        // Bloc final vide après le dernier morceau vidé : un flux Brotli complet.
        input[at] = 3;
        return BrotliDecode(input);
      };
      let n = list.length, out;
      try { out = attempt(n); } catch (e) {
        let lo = 0, hi = n - 1;
        while (lo < hi) { const mid = (lo + hi + 1) >> 1; try { attempt(mid); lo = mid; } catch (e2) { hi = mid - 1; } }
        n = lo;
        out = n ? attempt(n) : new Int8Array(0);
      }
      const bodies = [];
      for (let j = 0, offset = 0; j < list.length; j++) {
        const length = list[j].length;
        if (j < n && offset + length <= out.length) { bodies.push(new Uint8Array(out.buffer, out.byteOffset + offset, length)); offset += length; }
        else bodies.push(null);
      }
      interval = { key: k, bodies };
    };
    const brotliBody = index => {
      let k = index;
      while (k > 0 && !frames[k].key) k--;
      if (interval.key !== k) decodeInterval(k);
      return interval.bodies[index - k] || null;
    };

    // Écran (pixels 0x00RRGGBB, base des copies et des différences) et image affichée (RGBA) ; une image qui ne suit pas la
    // précédente (numéro) ou illisible arrête la lecture jusqu'à l'image clé suivante.
    let screen = null, next = -1, valid = false;
    const paint = (f, body) => {
      if (f.key) {
        tiles = new Map();
        if (f.w !== width || f.h !== height) {
          width = f.w; height = f.h; canvas.width = width; canvas.height = height;
          image = ctx.createImageData(width, height);
          pixels = new Uint32Array(image.data.buffer);
          screen = new Uint32Array(width * height);
        }
        valid = true;
      } else if (f.seq !== next || f.w !== width || f.h !== height) valid = false;
      next = f.seq + 1;
      if (!valid || !image) return;
      if (!body || body.length !== f.length) { valid = false; return; }
      const b = new DataView(body.buffer, body.byteOffset, body.byteLength);
      const size = f.size, px = size * size, bpp = f.tc ? 3 : 2, placesAt = f.moves * 12, basesAt = placesAt + f.places * 8, at = basesAt + f.count * 4;
      const columns = Math.ceil(width / size), cells = columns * Math.ceil(height / size);
      // Copies d'écran (défilement, fenêtre déplacée) d'abord, sur l'écran et sur l'image.
      for (let i = 0; i < f.moves; i++) {
        const m = i * 12, sx = b.getUint16(m, true), sy = b.getUint16(m + 2, true), w = b.getUint16(m + 4, true), mh = b.getUint16(m + 6, true);
        const dx = b.getUint16(m + 8, true), dy = b.getUint16(m + 10, true);
        if (!w || !mh || sx + w > width || dx + w > width || sy + mh > height || dy + mh > height) { valid = false; return; }
        for (let r = 0; r < mh; r++) {
          const row = dy > sy ? mh - 1 - r : r, from = (sy + row) * width + sx, to = (dy + row) * width + dx;
          screen.copyWithin(to, from, from + w);
          pixels.copyWithin(to, from, from + w);
        }
      }
      const each = (index, visit) => {
        const x0 = (index % columns) * size, y0 = Math.floor(index / columns) * size;
        for (let y = 0; y < size && y0 + y < height; y++)
          for (let x = 0; x < size && x0 + x < width; x++) visit(y * size + x, (y0 + y) * width + x0 + x);
      };
      const fresh = [];
      for (let i = 0; i < f.count; i++) {
        const tile = new Uint32Array(px), base = b.getUint32(basesAt + i * 4, true), delta = base !== 0xFFFFFFFF;
        if (delta && (f.key || base >= cells)) { valid = false; return; }
        if (bpp === 3) {
          for (let k = 0, o = at + i * px * 3; k < px; k++, o += 3) tile[k] = body[o] | (body[o + 1] << 8) | (body[o + 2] << 16);
          if (delta) each(base, (k, o) => { tile[k] ^= screen[o]; });
        } else {
          // RGB565 : différence dans cet espace (l'écran ramené en 16 bits), puis élargi.
          const narrow = new Uint16Array(px);
          for (let k = 0; k < px; k++) narrow[k] = b.getUint16(at + (i * px + k) * 2, true);
          if (delta) each(base, (k, o) => { narrow[k] ^= to565(screen[o]); });
          for (let k = 0; k < px; k++) tile[k] = from565(narrow[k]);
        }
        fresh.push(tile);
      }
      fresh.forEach((tile, i) => tiles.set(f.first + i, tile));
      for (let i = 0; i < f.places; i++) {
        const index = b.getUint32(placesAt + i * 8, true), tile = tiles.get(b.getUint32(placesAt + i * 8 + 4, true));
        if (!tile || index >= cells) { valid = false; return; }
        each(index, (k, o) => { const v = tile[k]; screen[o] = v; pixels[o] = rgba(v); });
      }
    };

    // Affichage en pause : écran assombri, « pause » et sa raison par-dessus (l'image d'avant la pause reste dessous).
    const veil = kind => {
      ctx.fillStyle = 'rgba(0,0,0,0.6)';
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      const size = Math.max(16, Math.round(canvas.height / 10)), x = canvas.width / 2, y = canvas.height / 2;
      ctx.fillStyle = '#fff';
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      ctx.font = 'bold ' + size + 'px sans-serif';
      ctx.fillText(words.paused, x, y - size * 0.4);
      ctx.font = Math.round(size / 2.5) + 'px sans-serif';
      ctx.fillText(kind === 2 ? words.idle : words.hidden, x, y + size * 0.5);
    };

    const indexAt = ms => {
      let lo = 0, hi = frames.length - 1, found = -1;
      while (lo <= hi) { const mid = (lo + hi) >> 1; if (frames[mid].t <= ms) { found = mid; lo = mid + 1; } else hi = mid - 1; }
      return found;
    };

    // Va à l'image voulue : depuis l'image clé précédente, ou à la suite ; les images au format 2 sont décompressées par lots.
    const run = async () => {
      running = true;
      try {
        while (wanted >= 0 && wanted !== current) {
          const target = wanted;
          let key = target;
          while (key > 0 && !frames[key].key) key--;
          const from = target < current || key > current ? key : current + 1;
          for (let i = from; i <= target; i += 32) {
            const batch = frames.slice(i, Math.min(target + 1, i + 32));
            const bodies = await Promise.all(batch.map((f, j) => f.fmt === 3 ? brotliBody(i + j) : inflate(f.p.subarray(f.hlen)).catch(() => null)));
            batch.forEach((f, j) => paint(f, bodies[j]));
          }
          current = target;
          if (image) ctx.putImageData(image, 0, 0);
          if (image && frames[target].pause) veil(frames[target].pause);
          gap.hidden = valid;
        }
      } finally { running = false; }
    };

    const show = ms => {
      position = Math.max(0, Math.min(duration, ms));
      seek.value = String(position);
      clock.textContent = clockOf(start + position / 1000);
      wanted = Math.max(indexAt(position), 0);
      if (!running) run();
    };

    const drawGraph = () => {
      const w = Math.max(1, Math.round(graph.clientWidth || 600)), h = graph.height;
      graph.width = w;
      const peaks = new Float32Array(w);
      for (const f of frames) {
        const i = Math.min(w - 1, Math.floor(f.t / duration * w));
        peaks[i] = Math.max(peaks[i], f.activity / 1000);
      }
      g.clearRect(0, 0, w, h);
      g.fillStyle = 'rgba(140,149,159,0.45)';
      for (const p of pauses) {
        const x = Math.floor(p.from / duration * w);
        g.fillRect(x, 0, Math.max(2, Math.ceil(p.to / duration * w) - x), h);
      }
      g.fillStyle = '#1565c0';
      for (let i = 0; i < w; i++) {
        if (peaks[i] > 0) { const bar = Math.max(2, Math.round(peaks[i] * (h - 4))); g.fillRect(i, h - 2 - bar, 2, bar); }
      }
    };

    const tick = now => {
      if (!playing) return;
      const ms = position + (now - last) * Number(speed.value);
      last = now;
      if (ms >= duration) { playing = false; play.textContent = words.play; }
      show(ms);
      if (playing) requestAnimationFrame(tick);
    };

    play.addEventListener('click', () => {
      playing = !playing;
      play.textContent = playing ? words.pause : words.play;
      if (playing) { if (position >= duration) show(0); last = performance.now(); requestAnimationFrame(tick); }
    });
    seek.addEventListener('input', () => show(Number(seek.value)));
    for (const mark of section.querySelectorAll('.mark')) mark.addEventListener('click', () => show(Number(mark.dataset.ms)));
    addEventListener('resize', drawGraph);
    drawGraph();
    show(0);
  }
""";

    /// <summary>Texte échappé comme dans le reste du rapport : le nom de la session vient des serveurs.</summary>
    private static string Encode(string text) => InterventionReport.Encode(text);
}
