using ZillaTerm.Core.Interventions;

namespace ZillaTerm.Core.Tests.Interventions;

public sealed class InterventionReminderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "zillaterm-reminders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Texte des Paramètres : une ligne par rappel, minutes puis texte ; rangés par heure, sans doublon.</summary>
    [Fact]
    public void RemindersAreReadFromLines()
    {
        Assert.True(InterventionReminder.TryParse("30 Escalader au N3\r\n\r\n  15   Prévenir le responsable  \n30 Escalader au N3", out var reminders, out int bad));
        Assert.Equal(0, bad);
        Assert.Equal([new InterventionReminder(15, "Prévenir le responsable"), new InterventionReminder(30, "Escalader au N3")], reminders);
        Assert.Equal("15 Prévenir le responsable\n30 Escalader au N3", InterventionReminder.Format(reminders));
        Assert.True(InterventionReminder.TryParse("", out var none, out _));
        Assert.Empty(none);

        foreach (var (text, line) in new[]
        {
            ("Escalader", 1), ("15 ok\n0 trop tôt", 2), ("1441 demain", 1), ("-5 avant", 1), ("30", 1), ("30 ", 1),
            ("10 a‮b", 1), ("5 " + new string('x', InterventionReminder.MaxText + 1), 1),
            (string.Join("\n", Enumerable.Range(1, InterventionReminder.MaxReminders + 1).Select(i => $"{i} rappel {i}")), 11),
        })
        {
            Assert.False(InterventionReminder.TryParse(text, out var refused, out bad), text);
            Assert.Equal(line, bad);
            Assert.Empty(refused);
        }
    }

    /// <summary>Diffusés par le fichier d'environnement : vérifiés, montrés comme changement, appliqués à la place.</summary>
    [Fact]
    public void RemindersComeFromTheEnvironment()
    {
        var settings = new AppSettings { InterventionReminders = [new(30, "Escalader au N3")] };
        var profile = EnvironmentProfile.FromSettings(settings, null);
        Assert.Equal(settings.InterventionReminders, profile.InterventionReminders);
        Assert.Null(EnvironmentProfile.FromSettings(new AppSettings(), null).InterventionReminders);

        var target = new AppSettings { InterventionReminders = [new(10, "ancien")] };
        var change = Assert.Single(profile.Diff(target), c => c.Setting == EnvironmentSetting.InterventionReminders);
        Assert.Equal("10 ancien", change.Current);
        Assert.Equal("30 Escalader au N3", change.New);
        profile.ApplyTo(target);
        Assert.Equal(settings.InterventionReminders, target.InterventionReminders);
        Assert.DoesNotContain(profile.Diff(target), c => c.Setting == EnvironmentSetting.InterventionReminders);
        new EnvironmentProfile { InterventionReminders = [] }.ApplyTo(target);
        Assert.Empty(target.InterventionReminders);

        foreach (var bad in new List<InterventionReminder?>[]
        {
            [new(0, "zéro")], [new(30, "")], [new(30, "a‮b")], [null],
            [.. Enumerable.Range(1, InterventionReminder.MaxReminders + 1).Select(i => new InterventionReminder(i, "r"))],
        })
        {
            var refused = Assert.Throws<EnvironmentFileException>(new EnvironmentProfile { InterventionReminders = bad! }.Validate);
            Assert.Equal(EnvironmentProblem.InvalidReminder, refused.Problem);
        }

        // Le fichier JSON tel qu'une équipe l'écrit.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, EnvironmentProfile.FileName);
        File.WriteAllText(path, """{ "Format": 1, "InterventionReminders": [ { "Minutes": 45, "Text": "Point avec le responsable" } ] }""");
        Assert.Equal([new InterventionReminder(45, "Point avec le responsable")], EnvironmentProfile.Load(path).Profile.InterventionReminders);
    }

    /// <summary>Gardés dans les réglages ; un rappel invalide (fichier modifié à la main) est écarté à la lecture.</summary>
    [Fact]
    public void RemindersAreSavedWithTheSettings()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        new AppSettings { InterventionReminders = [new(30, "Escalader au N3"), new(15, "Prevenir")] }.Save(path);
        Assert.Equal([new InterventionReminder(15, "Prevenir"), new InterventionReminder(30, "Escalader au N3")],
            AppSettings.Load(path).InterventionReminders);

        var json = File.ReadAllText(path).Replace("\"Prevenir\"", "\"\"", StringComparison.Ordinal);
        File.WriteAllText(path, json);
        Assert.Equal([new InterventionReminder(30, "Escalader au N3")], AppSettings.Load(path).InterventionReminders);
    }
}
