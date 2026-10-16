using System.Globalization;
using ZillaTerm.Core.Interventions;

namespace ZillaTerm.Core.Tests.Interventions;

public sealed class InterventionRedactionTests
{
    private const string Mask = "[masked]";

    private static readonly DateTime T0 = new(2026, 10, 14, 2, 0, 0, DateTimeKind.Utc);

    private static string Line(string text)
    {
        var result = InterventionRedaction.Redact(text, Mask, out _);
        return result;
    }

    /// <summary>Les valeurs des mots de passe, secrets, jetons et clés d'API sont masquées, quelle que soit la syntaxe.</summary>
    [Theory]
    [InlineData("password=S3cr3t!", "password=[masked]")]
    [InlineData("DB_PASSWORD=\"S3cr3t\" ./run.sh", "DB_PASSWORD=\"[masked]\" ./run.sh")]
    [InlineData("export MYSQL_PWD='p@ss w'", "export MYSQL_PWD='[masked]'")]
    [InlineData("  \"client_secret\": \"abcdef123\",", "  \"client_secret\": \"[masked]\",")]
    [InlineData("mysqldump --user=root --password=Tr0ub4dor db", "mysqldump --user=root --password=[masked] db")]
    [InlineData("Server=db01;User Id=sa;Password=Pa55;Pwd=Other1;", "Server=db01;User Id=sa;Password=[masked];Pwd=[masked];")]
    [InlineData("api_key: 9f86d081884c7d659a2f", "api_key: [masked]")]
    [InlineData("  password: s3cr3t # prod", "  password: [masked] # prod")]
    [InlineData("X-Api-Key: Zm9vYmFy", "X-Api-Key: [masked]")]
    [InlineData("AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG", "AWS_SECRET_ACCESS_KEY=[masked]")]
    [InlineData("curl https://jdoe:Winter2026@srv01.corp.local/api", "curl https://jdoe:[masked]@srv01.corp.local/api")]
    [InlineData("Authorization: Bearer 0123456789abcdefXYZ", "Authorization: Bearer [masked]")]
    [InlineData("-H 'Authorization: Basic YWRtaW46YWRtaW4='", "-H 'Authorization: Basic [masked]'")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ0123456789", "token [masked]")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJqZG9lIn0.c2lnbmF0dXJlX2hlcmU", "jwt [masked]")]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE", "aws_access_key_id = [masked]")]
    [InlineData("root:$6$rounds=5000$saltsalt$Xk3h2J8sZ0aQ1bC2dE3fG4hI5jK6lM7nO8pQ9rS0tU:19650:0:99999:7:::",
        "root:[masked]:19650:0:99999:7:::")]
    // Mot de passe qui commence par « $ » ou « % » : une valeur, pas une variable.
    [InlineData("mysql -u root --password='$ecr3t!Prod'", "mysql -u root --password='[masked]'")]
    [InlineData("password: $Tr0ub4dor!3", "password: [masked]")]
    [InlineData("db_password=%Xk9!pq", "db_password=[masked]")]
    [InlineData("DB_PASSWORD=\"$Pa55;w0rd\"", "DB_PASSWORD=\"[masked]\"")]
    [InlineData("jdoe:$y$j9T$abcdefgh$0123456789abcdefghijklmnopqrstuvwxyzABCDE:19000::::::", "jdoe:[masked]:19000::::::")]
    public void SecretsAreMasked(string line, string expected) => Assert.Equal(expected, Line(line));

    /// <summary>Ce qui n'est pas un secret reste lisible : invites, messages, variables, gabarits, chemins.</summary>
    [Theory]
    [InlineData("[sudo] password for jdoe:")]
    [InlineData("Password:")]
    [InlineData("passwd: all authentication tokens updated successfully.")]
    [InlineData("PWD=/home/jdoe")]
    [InlineData("OLDPWD=~/logs")]
    [InlineData("password_file=/etc/app/secret.txt")]
    [InlineData("PasswordAuthentication yes")]
    [InlineData("Basic authentication is disabled")]
    [InlineData("password: ********")]
    [InlineData("DB_PASSWORD=$DB_PASS")]
    [InlineData("export DB_PASSWORD=${VAULT_PASS}")]
    [InlineData("export API_TOKEN=$(cat /run/secrets/token)")]
    [InlineData("set DB_PASSWORD=%DB_PASS%")]
    [InlineData("password: \"{{ vault_db_password }}\"")]
    [InlineData("token_expiry=3600")]
    [InlineData("[admin-srv01@srv01 ~]$ df -h /var")]
    [InlineData("Filesystem      Size  Used Avail Use% Mounted on")]
    public void OrdinaryTextIsKept(string line)
    {
        Assert.Equal(line, InterventionRedaction.Redact(line, Mask, out int count));
        Assert.Equal(0, count);
    }

    /// <summary>
    /// Clé privée affichée dans un terminal (une ligne par événement) : l'en-tête et la fin restent, le corps devient une
    /// seule ligne masquée ; l'invite qui suit est gardée. Une clé coupée (sans fin) s'arrête à la première ligne qui n'est
    /// pas du base64.
    /// </summary>
    [Fact]
    public void PrivateKeyBlocksAreMaskedAcrossTerminalLines()
    {
        string[] lines =
        [
            "[root@srv01 ~]# cat .ssh/id_rsa",
            "-----BEGIN OPENSSH PRIVATE KEY-----",
            "b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW",
            "QyNTUxOQAAACDLkF1y0bE7j2yY0l2Q6w3h4k5l6m7n8o9p0q1r2s3t4uAAAAJgAAAAAAA",
            "AAAAC3NzaC1lZDI1NTE5AAAAIMuQXXLRsTuPbJjSXZDrDeHiTmXqbvyjz2nSrWvazO3i==",
            "-----END OPENSSH PRIVATE KEY-----",
            "[root@srv01 ~]# head -2 /etc/ssl/private/srv01.key",
            "-----BEGIN RSA PRIVATE KEY-----",
            "MIIEpAIBAAKCAQEA1x2y3z4a5b6c7d8e9f0g1h2i3j4k5l6m7n8o9p0q1r2s3t4u5v6",
            "[root@srv01 ~]# echo done",
            "done",
        ];
        var entries = new List<InterventionEntry> { new(T0, InterventionKind.Started, "", "jdoe") };
        entries.AddRange(lines.Select((l, i) => new InterventionEntry(T0.AddSeconds(i + 1), InterventionKind.Terminal, "root@srv01", l)));
        // Une autre session pendant la clé : son texte n'est pas pris pour le corps de la clé.
        entries.Insert(4, new InterventionEntry(T0.AddSeconds(3.5), InterventionKind.Terminal, "jdoe@srv02", "uptime"));
        var recording = new InterventionRecording("x", entries, false, false);

        var redacted = InterventionRedaction.Apply(recording, Mask, out int count);

        Assert.Equal(2, count);
        Assert.Equal(
        [
            "jdoe",
            "[root@srv01 ~]# cat .ssh/id_rsa",
            "-----BEGIN OPENSSH PRIVATE KEY-----",
            Mask,
            "uptime",
            "-----END OPENSSH PRIVATE KEY-----",
            "[root@srv01 ~]# head -2 /etc/ssl/private/srv01.key",
            "-----BEGIN RSA PRIVATE KEY-----",
            Mask,
            "[root@srv01 ~]# echo done",
            "done",
        ], redacted.Entries.Select(e => e.Text));
        Assert.DoesNotContain(redacted.Entries, e => e.Text.Contains("b3BlbnNzaC1", StringComparison.Ordinal) || e.Text.Contains("MIIE", StringComparison.Ordinal));
    }

    /// <summary>Notes, titre, transferts : masqués aussi (une clé collée dans une note, un mot de passe dans le titre).</summary>
    [Fact]
    public void NotesTitleAndTransfersAreMasked()
    {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        var recording = new InterventionRecording("x",
        [
            new(T0, InterventionKind.Started, "", "jdoe"),
            new(T0.AddSeconds(1), InterventionKind.Title, "", "INC0012345 password=Hunter2"),
            new(T0.AddSeconds(2), InterventionKind.Note, "", "clé :\n-----BEGIN EC PRIVATE KEY-----\nMHcCAQEEIBc2\nAoGCCqGSM49\n-----END EC PRIVATE KEY-----\nfin"),
            new(T0.AddSeconds(3), InterventionKind.Transfer, "srv01", "https://admin:Adm1n@srv01.corp.local/app.war — terminé"),
            new(T0.AddSeconds(4), InterventionKind.Screenshot, "srv01", "capture password=Hunter2", png),
            new(T0.AddSeconds(5), InterventionKind.Stopped, "", ""),
        ], false, false);

        var redacted = InterventionRedaction.Apply(recording, Mask, out int count);

        Assert.Equal(4, count);
        Assert.Equal("INC0012345 password=[masked]", redacted.Title);
        Assert.Equal("clé :\n-----BEGIN EC PRIVATE KEY-----\n[masked]\n-----END EC PRIVATE KEY-----\nfin", redacted.Entries[2].Text);
        Assert.Equal("https://admin:[masked]@srv01.corp.local/app.war — terminé", redacted.Entries[3].Text);
        Assert.Same(png, redacted.Entries[4].Image);

        var html = InterventionReport.ToHtml(redacted, new InterventionReportText(), TimeZoneInfo.Utc, CultureInfo.InvariantCulture, count);
        Assert.DoesNotContain("Hunter2", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Adm1n", html, StringComparison.Ordinal);
        Assert.Contains("Secrets masked: 4 passage(s)", html, StringComparison.Ordinal);

        var plain = InterventionReport.ToHtml(recording, new InterventionReportText(), TimeZoneInfo.Utc, CultureInfo.InvariantCulture);
        Assert.Contains("Hunter2", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("Secrets masked", plain, StringComparison.Ordinal);
    }

    /// <summary>Une ligne démesurée ne bloque pas l'export : elle est examinée à temps ou masquée entière.</summary>
    [Fact]
    public void HugeLinesStayFast()
    {
        var line = "password=" + new string('a', 2_000_000) + " " + string.Concat(Enumerable.Repeat("x=y ", 200_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = InterventionRedaction.Redact(line, Mask, out int count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.True(count >= 1);
        Assert.DoesNotContain(new string('a', 600), result, StringComparison.Ordinal);
    }
}
