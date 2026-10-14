using System.Diagnostics.CodeAnalysis;

// DpapiProtector est lié tel quel depuis ZillaTerm.App (Windows seulement, où l'analyseur ne dit rien) : ici, multiplateforme,
// l'analyseur de compatibilité signale ses appels à DPAPI. Il n'est créé que derrière OperatingSystem.IsWindows()
// (SecretProtectors), seul endroit qui le construit.
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Scope = "type",
    Target = "~T:ZillaTerm.App.Services.KeePass.DpapiProtector",
    Justification = "Créé uniquement sous Windows, derrière OperatingSystem.IsWindows() (SecretProtectors).")]
