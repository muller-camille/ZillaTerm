using System.Text;
using ZillaTerm.Core.Migration;

namespace ZillaTerm.Core.Tests.Migration;

/// <summary>Lecture des sessions des autres logiciels : dossiers, serveur, compte, port, protocole ; jamais de mot de passe.</summary>
public sealed class SessionReaderTests
{
    private const string RegExport = """
        Windows Registry Editor Version 5.00

        [HKEY_CURRENT_USER\Software\SimonTatham\PuTTY\Sessions\Default%20Settings]
        "HostName"=""

        [HKEY_CURRENT_USER\Software\SimonTatham\PuTTY\Sessions\Prod/Web/web%20front]
        "HostName"="web01.corp.local"
        "PortNumber"=dword:00000016
        "Protocol"="ssh"
        "UserName"="root"
        "PublicKeyFile"="C:\\keys\\id.ppk"

        [HKEY_CURRENT_USER\Software\SimonTatham\PuTTY\Sessions\via%20psmp]
        "HostName"="psmp.corp.local"
        "UserName"="jdoe@oracle@db01.corp.local"
        "Protocol"="ssh"

        [HKEY_CURRENT_USER\Software\SimonTatham\PuTTY\Sessions\Console]
        "HostName"="COM1"
        "Protocol"="serial"

        [HKEY_CURRENT_USER\Software\SimonTatham\PuTTY\Sessions\template]
        "Protocol"="ssh"

        [HKEY_CURRENT_USER\Software\9bis.com\KiTTY\Sessions\routeur]
        "HostName"="admin@sw01"
        "Protocol"="telnet"
        "Password"="secret"
        "Folder"="Reseau"

        [HKEY_CURRENT_USER\Software\Martin Prikryl\WinSCP 2\Sessions\Logs/app01]
        "HostName"="app01"
        "UserName"="deploy"
        "FSProtocol"=dword:00000002
        "Password"="A35C4D5B"
        "Data"=hex:01,02,03,\
          04,05

        [HKEY_CURRENT_USER\Software\Martin Prikryl\WinSCP 2\Sessions\ftp%20site]
        "HostName"="ftp.example.com"
        "FSProtocol"=dword:00000005

        [-HKEY_CURRENT_USER\Software\SimonTatham\PuTTY\Sessions\removed]
        "HostName"="gone"
        """;

    [Fact]
    public void ReadsPuttyKittyAndWinScpFromARegistryExport()
    {
        var path = Write("export.reg", RegExport, Encoding.Unicode);
        List<ImportedSession> sessions;
        try
        {
            sessions = SessionSources.Read(ImportSourceKind.RegFile, path);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }

        Assert.Equal(["web front", "via psmp", "Console", "routeur", "app01", "ftp site"], sessions.Select(s => s.Name));

        var web = sessions[0];
        Assert.Equal("Prod/Web", web.Folder);
        Assert.Equal(ImportProtocol.Ssh, web.Protocol);
        Assert.Equal("web01.corp.local", web.Host);
        Assert.Equal(22, web.Port);
        Assert.Equal("root", web.User);
        Assert.False(web.ViaPsm);

        // Session qui passait par le PSMP : le compte et le serveur cibles, pas le PSMP.
        var psmp = sessions[1];
        Assert.True(psmp.ViaPsm);
        Assert.Equal("db01.corp.local", psmp.Host);
        Assert.Equal("oracle", psmp.User);

        Assert.Equal(ImportProtocol.Other, sessions[2].Protocol);
        Assert.Equal("serial", sessions[2].ProtocolName);

        var router = sessions[3];
        Assert.Equal(ImportProtocol.Telnet, router.Protocol);
        Assert.Equal("Reseau", router.Folder);
        Assert.Equal(("sw01", "admin"), (router.Host, router.User));

        var app = sessions[4];
        Assert.Equal((ImportProtocol.Sftp, "Logs", "app01", "deploy"), (app.Protocol, app.Folder, app.Host, app.User));
        Assert.Equal(("FTP", ImportProtocol.Other), (sessions[5].ProtocolName, sessions[5].Protocol));
    }

    [Fact]
    public void KeepsOnlyTheRegistryValuesItNeeds()
    {
        var keys = RegFile.Parse(RegExport, PuttySessions.ValueNames.Concat(WinScpSites.ValueNames));
        Assert.DoesNotContain(keys, k => k.Values.ContainsKey("Password") || k.Values.ContainsKey("PublicKeyFile") || k.Values.ContainsKey("Data"));
        Assert.DoesNotContain(keys, k => k.Path.Contains("removed"));
        Assert.Equal(22, keys.Single(k => k.Path.EndsWith("web%20front")).GetInt("PortNumber"));

        Assert.Throws<InvalidDataException>(() => RegFile.Parse("[HKEY_CURRENT_USER\\x]\n\"a\"=\"b\"", ["a"]));
    }

    [Fact]
    public void ReadsKittyPortableSessionFiles()
    {
        var root = Directory.CreateTempSubdirectory("zt-kitty-").FullName;
        try
        {
            var sessions = Directory.CreateDirectory(Path.Combine(root, "Sessions", "Prod%20Unix")).FullName;
            File.WriteAllText(Path.Combine(sessions, "lnx01"), "HostName\\lnx01.corp.local\\\r\nPortNumber\\2222\\\r\nUserName\\jean%20dupont\\\r\nPassword\\secret\\\r\nProtocol\\ssh\\\r\n");
            File.WriteAllText(Path.Combine(root, "Sessions", "Default%20Settings"), "HostName\\x\\\n");

            var values = PuttySessions.ParseKittyFile(File.ReadAllText(Path.Combine(sessions, "lnx01")));
            Assert.False(values.ContainsKey("Password"));

            var read = SessionSources.Read(ImportSourceKind.KittyFolder, root);
            var single = Assert.Single(read);
            Assert.Equal(("Prod Unix", "lnx01", "lnx01.corp.local", 2222, "jean dupont"), (single.Folder, single.Name, single.Host, single.Port, single.User));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsPuttyUnixSessionFiles()
    {
        var root = Directory.CreateTempSubdirectory("zt-putty-").FullName;
        try
        {
            var sessions = Directory.CreateDirectory(Path.Combine(root, "sessions")).FullName;
            // Nom encodé (« Prod/web 01 ») : la partie avant « / » devient le dossier, comme pour le registre.
            File.WriteAllText(Path.Combine(sessions, "Prod%2Fweb%2001"),
                "HostName=web01.corp.local\nPortNumber=2222\nUserName=jdoe\nPassword=secret\nProtocol=ssh\nTerminalType=xterm=256\n");
            File.WriteAllText(Path.Combine(sessions, "Default%20Settings"), "HostName=x\n");
            File.WriteAllText(Path.Combine(sessions, "serie"), "HostName=/dev/ttyS0\nProtocol=serial\n");

            var values = PuttySessions.ParseUnixFile(File.ReadAllText(Path.Combine(sessions, "Prod%2Fweb%2001")));
            Assert.False(values.ContainsKey("Password"));
            Assert.False(values.ContainsKey("TerminalType"));

            var read = SessionSources.Read(ImportSourceKind.PuttyFolder, root).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            Assert.Equal(2, read.Count);
            var web = read.Single(s => s.Name == "web 01");
            Assert.Equal(("Prod", "web01.corp.local", 2222, "jdoe", ImportProtocol.Ssh), (web.Folder, web.Host, web.Port, web.User, web.Protocol));
            Assert.Equal(ImportProtocol.Other, read.Single(s => s.Name == "serie").Protocol);
            Assert.True(SessionSources.IsFolder(ImportSourceKind.PuttyFolder));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsAWinScpIniFile()
    {
        var sessions = WinScpSites.FromIni("""
            [Configuration\Interface]
            Foo=1
            [Sessions\Prod/db%20server]
            HostName=db01
            UserName=oracle
            PortNumber=2222
            Password=A35C
            [Sessions\Default%20Settings]
            HostName=x
            [Sessions\work]
            IsWorkspace=1
            HostName=y
            [Sessions\scp]
            HostName=old01
            FSProtocol=0
            """);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(("Prod", "db server", "db01", 2222, "oracle", ImportProtocol.Sftp),
            (sessions[0].Folder, sessions[0].Name, sessions[0].Host, sessions[0].Port, sessions[0].User, sessions[0].Protocol));
        Assert.Equal(ImportProtocol.Sftp, sessions[1].Protocol);
    }

    [Fact]
    public void CraftedFilesAreRefusedWithoutCrashing()
    {
        // Dossiers imbriqués des milliers de fois : refusé avant tout parcours récursif.
        var nested = new StringBuilder("<?xml version=\"1.0\"?><mrng:Connections xmlns:mrng=\"http://mremoteng.org\" Name=\"Connections\">");
        for (int i = 0; i < 5_000; i++)
        {
            nested.Append("<Node Name=\"a\" Type=\"Container\">");
        }

        for (int i = 0; i < 5_000; i++)
        {
            nested.Append("</Node>");
        }

        nested.Append("</mrng:Connections>");
        Assert.Throws<InvalidDataException>(() => MRemoteNgFile.Read(nested.ToString()));

        // Chiffres d'une autre écriture dans une définition : ligne ignorée, pas d'exception.
        var sessions = MxtSessionsFile.Read("[Bookmarks]\nSubRep=\nweb01=#\u0661\u0660\u0669#0%web01.corp.local%22%root\n");
        Assert.Empty(sessions);

        // Motif plein d'étoiles : réponse immédiate, sans expression régulière qui s'emballe.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var config = OpenSshConfig.Read("Host " + string.Concat(Enumerable.Repeat("*a", 40)) + "b\n  HostName srv01.corp.local\n"
            + "Host web01\n  User root\n");
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Single(config);
    }

    [Theory]
    [InlineData("web*", "web01", true)]
    [InlineData("WEB0?", "web01", true)]
    [InlineData("*.corp.local", "srv01.corp.local", true)]
    [InlineData("*", "", true)]
    [InlineData("web?", "web01", false)]
    [InlineData("a*b*c", "aXbYc", true)]
    [InlineData("a*b*c", "aXbY", false)]
    public void OpenSshPatternsMatchLikeOpenSsh(string pattern, string alias, bool expected) =>
        Assert.Equal(expected, OpenSshConfig.Glob(pattern, alias));

    [Fact]
    public void ReadsAnMxtSessionsFile()
    {
        var sessions = MxtSessionsFile.Read("""
            [Bookmarks]
            SubRep=
            ImgNum=42
            [Bookmarks_1]
            SubRep=Prod\Linux
            ImgNum=41
            web01=#109#0%web01.corp.local%22%root%%-1%-1%%%22%%0%0%0%%%-1%0%0%0%%1080%%0%0%1#Font%10%0#0# #-1
            files=#140#7%web01.corp.local%2222%deploy%%-1#0# #-1
            [Bookmarks_2]
            SubRep=Prod\Windows
            ImgNum=41
            dc01=#91#4%dc01.corp.local%3389%CORP\admin%0%-1%-1#0# #-1
            vnc=#128#5%vnc01%5900%%-1#0# #-1
            custom icon=#12#99%sw01%22%admin%%-1#0# #-1
            [Passwords]
            root@web01=secret
            """);

        Assert.Equal(["web01", "files", "dc01", "vnc", "custom icon"], sessions.Select(s => s.Name));
        Assert.Equal(("Prod/Linux", ImportProtocol.Ssh, "web01.corp.local", 22, "root"),
            (sessions[0].Folder, sessions[0].Protocol, sessions[0].Host, sessions[0].Port, sessions[0].User));
        Assert.Equal((ImportProtocol.Sftp, 2222, "deploy"), (sessions[1].Protocol, sessions[1].Port, sessions[1].User));
        Assert.Equal(("Prod/Windows", ImportProtocol.Rdp, "dc01.corp.local", "admin", "CORP"),
            (sessions[2].Folder, sessions[2].Protocol, sessions[2].Host, sessions[2].User, sessions[2].Domain));
        Assert.Equal(("VNC", ImportProtocol.Other), (sessions[3].ProtocolName, sessions[3].Protocol));
        Assert.Equal(ImportProtocol.Other, sessions[4].Protocol);
    }

    [Fact]
    public void ReadsMRemoteNgConnectionsWithInheritance()
    {
        var sessions = MRemoteNgFile.Read("""
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" FullFileEncryption="false" Protected="abc" ConfVersion="2.6">
              <Node Name="Prod" Type="Container" Username="admin" Domain="CORP" Hostname="" Protocol="RDP">
                <Node Name="dc01" Type="Connection" Hostname="dc01.corp.local" Protocol="RDP" Port="3390" Username="" Domain=""
                      InheritUsername="true" InheritDomain="true" Password="xyz" />
                <Node Name="web01" Type="Connection" Hostname="web01" Protocol="SSH2" Port="22" Username="root" Domain="" />
                <Node Name="Sous dossier" Type="Container">
                  <Node Name="vnc" Type="Connection" Hostname="vnc01" Protocol="VNC" Port="5900" />
                </Node>
              </Node>
            </mrng:Connections>
            """);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(("Prod", ImportProtocol.Rdp, "dc01.corp.local", 3390, "admin", "CORP"),
            (sessions[0].Folder, sessions[0].Protocol, sessions[0].Host, sessions[0].Port, sessions[0].User, sessions[0].Domain));
        Assert.Equal((ImportProtocol.Ssh, "root", (string?)null), (sessions[1].Protocol, sessions[1].User, sessions[1].Domain));
        Assert.Equal(("Prod/Sous dossier", ImportProtocol.Other), (sessions[2].Folder, sessions[2].Protocol));

        var encrypted = """<Connections FullFileEncryption="true" ConfVersion="2.6">c2VjcmV0</Connections>""";
        Assert.Throws<InvalidDataException>(() => MRemoteNgFile.Read(encrypted));
        Assert.Throws<InvalidDataException>(() => MRemoteNgFile.Read("<RDCMan />"));
    }

    [Fact]
    public void RefusesDocumentTypeDefinitions()
    {
        const string entity = """
            <?xml version="1.0"?>
            <!DOCTYPE Connections [ <!ENTITY x SYSTEM "file:///c:/windows/win.ini"> ]>
            <Connections><Node Name="a" Type="Connection" Hostname="&x;" Protocol="SSH2" /></Connections>
            """;
        Assert.Throws<InvalidDataException>(() => MRemoteNgFile.Read(entity));
    }

    [Fact]
    public void ReadsRdcManGroupsCredentialsAndPsmServers()
    {
        var sessions = RdcManFile.Read("""
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.90" schemaVersion="3">
              <file>
                <credentialsProfiles>
                  <credentialsProfile inherit="None"><profileName scope="Local">ops</profileName><userName>opsadmin</userName><password>x</password><domain>CORP</domain></credentialsProfile>
                </credentialsProfiles>
                <properties><expanded>True</expanded><name>Mon fichier</name></properties>
                <logonCredentials inherit="None"><profileName scope="Local">Custom</profileName><userName>admin</userName><password>enc</password><domain>CORP</domain></logonCredentials>
                <group>
                  <properties><expanded>True</expanded><name>Prod</name></properties>
                  <server><properties><displayName>Contrôleur</displayName><name>dc01.corp.local</name></properties></server>
                  <group>
                    <properties><name>Apps</name></properties>
                    <logonCredentials inherit="None"><profileName scope="File">ops</profileName></logonCredentials>
                    <server>
                      <properties><name>app01</name></properties>
                      <connectionSettings inherit="None"><connectToConsole>False</connectToConsole><startProgram /><port>3390</port></connectionSettings>
                    </server>
                    <server>
                      <properties><name>psm01.corp.local</name><displayName>via PSM</displayName></properties>
                      <connectionSettings inherit="None"><startProgram>psm /u localadmin /a app02.corp.local /c PSM-RDP</startProgram></connectionSettings>
                    </server>
                  </group>
                </group>
              </file>
            </RDCMan>
            """);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(("Prod", "Contrôleur", "dc01.corp.local", "admin", "CORP"),
            (sessions[0].Folder, sessions[0].Name, sessions[0].Host, sessions[0].User, sessions[0].Domain));
        Assert.Equal(("Prod/Apps", "app01", 3390, "opsadmin"), (sessions[1].Folder, sessions[1].Host, sessions[1].Port, sessions[1].User));
        Assert.Equal((true, "app02.corp.local", "localadmin", "PSM-RDP"),
            (sessions[2].ViaPsm, sessions[2].Host, sessions[2].User, sessions[2].Component));

        // Ancien format : le nom directement sous le groupe et le serveur.
        var old = RdcManFile.Read("""<RDCMan schemaVersion="1"><file><name>x</name><group><name>G</name><server><name>srv</name><displayName>S</displayName></server></group></file></RDCMan>""");
        Assert.Equal(("G", "S", "srv"), (old[0].Folder, old[0].Name, old[0].Host));
    }

    [Fact]
    public void ReadsOpenSshHostsLikeSshDoes()
    {
        var sessions = OpenSshConfig.Read("""
            # Serveurs
            Host web01 web02
                HostName %h.corp.local
                Port 2222
            Host db
                HostName=db01.corp.local
                User oracle
            Host via-psmp
                HostName psmp.corp.local
                User "jdoe@root@srv09.corp.local"
            Match host foo
                User matchuser
            Host *.corp.local !bastion*
                Port 2200
            Host *
                User admin
            """);

        Assert.Equal(["web01", "web02", "db", "via-psmp"], sessions.Select(s => s.Name));
        Assert.Equal(("web01.corp.local", 2222, "admin"), (sessions[0].Host, sessions[0].Port, sessions[0].User));
        Assert.Equal(("db01.corp.local", "oracle"), (sessions[2].Host, sessions[2].User));
        Assert.Equal((true, "srv09.corp.local", "root"), (sessions[3].ViaPsm, sessions[3].Host, sessions[3].User));
    }

    [Fact]
    public void ReadsSecureCrtSessionsFromFilesAndXml()
    {
        var root = Directory.CreateTempSubdirectory("zt-crt-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Sessions", "Prod")).FullName;
            File.WriteAllText(Path.Combine(folder, "lnx01.ini"), "S:\"Protocol Name\"=SSH2\r\nS:\"Hostname\"=lnx01.corp.local\r\nS:\"Username\"=root\r\nD:\"[SSH2] Port\"=00000016\r\nS:\"Password V2\"=02:abcdef\r\n", new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(folder, "__FolderData__.ini"), "S:\"Folder List\"=\r\n");
            File.WriteAllText(Path.Combine(root, "Sessions", "Default.ini"), "S:\"Hostname\"=x\r\n");

            var read = SessionSources.Read(ImportSourceKind.SecureCrtFolder, root);
            var single = Assert.Single(read);
            Assert.Equal(("Prod", "lnx01", "lnx01.corp.local", 22, "root"), (single.Folder, single.Name, single.Host, single.Port, single.User));
            Assert.False(SecureCrtSessions.ParseIni(File.ReadAllText(Path.Combine(folder, "lnx01.ini"))).ContainsKey("Password V2"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        var xml = SecureCrtSessions.FromXml("""
            <?xml version="1.0" encoding="UTF-8"?>
            <VanDyke version="3.0">
              <key name="Sessions">
                <key name="Default"><string name="Hostname"></string><string name="Protocol Name">SSH2</string></key>
                <key name="Prod">
                  <key name="lnx01">
                    <string name="Hostname">lnx01</string>
                    <string name="Protocol Name">SSH2</string>
                    <dword name="[SSH2] Port">2222</dword>
                    <string name="Username">admin</string>
                    <string name="Password V2">02:xxx</string>
                  </key>
                  <key name="sw01"><string name="Hostname">sw01</string><string name="Protocol Name">Telnet</string><dword name="Port">23</dword></key>
                  <key name="serial"><string name="Protocol Name">Serial</string></key>
                </key>
              </key>
            </VanDyke>
            """);
        Assert.Equal(2, xml.Count);
        Assert.Equal(("Prod", "lnx01", 2222, "admin"), (xml[0].Folder, xml[0].Host, xml[0].Port, xml[0].User));
        Assert.Equal((ImportProtocol.Telnet, 23), (xml[1].Protocol, xml[1].Port));
    }

    [Fact]
    public void ReadsRdpFilesIncludingPsmConnections()
    {
        var direct = RdpFiles.Parse("Prod", "rds", "screen mode id:i:2\r\nfull address:s:rds01.corp.local:3390\r\nusername:s:CORP\\jdoe\r\n");
        Assert.Equal(("rds01.corp.local", 3390, "jdoe", "CORP", false), (direct!.Host, direct.Port, direct.User, direct.Domain, direct.ViaPsm));

        var psm = RdpFiles.Parse("", "app", "full address:s:psm.corp.local\nusername:s:jdoe\nalternate shell:s:psm /u administrator /a 10.1.2.3 /c PSM-RDP\n");
        Assert.Equal(("10.1.2.3", "administrator", "PSM-RDP", true), (psm!.Host, psm.User, psm.Component, psm.ViaPsm));

        Assert.Null(RdpFiles.Parse("", "empty", "screen mode id:i:2\n"));

        var root = Directory.CreateTempSubdirectory("zt-rdp-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Windows"));
            File.WriteAllText(Path.Combine(root, "Windows", "dc01.rdp"), "full address:s:dc01\r\n", Encoding.Unicode);
            var read = Assert.Single(SessionSources.Read(ImportSourceKind.RdpFolder, root));
            Assert.Equal(("Windows", "dc01", "dc01"), (read.Folder, read.Name, read.Host));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("jdoe@corp.com@root#corp.local@srv01@psmp", null, "srv01", "root", "corp.local", true)]
    [InlineData("srv01", "jdoe@corp.local", "srv01", "jdoe", "corp.local", false)]
    [InlineData("root@srv01", null, "srv01", "root", null, false)]
    [InlineData("psmp", "jdoe@root@srv01", "srv01", "root", null, true)]
    [InlineData("srv01", "CORP\\admin", "srv01", "admin", "CORP", false)]
    [InlineData("srv01", null, "srv01", null, null, false)]
    public void DecodesUsersAndPsmpLogins(string host, string? user, string expectedHost, string? expectedUser, string? expectedDomain, bool viaPsm)
    {
        var session = ImportedSession.Terminal("", "x", ImportProtocol.Ssh, host, null, user);
        Assert.Equal((expectedHost, expectedUser, expectedDomain, viaPsm), (session.Host, session.User, session.Domain, session.ViaPsm));
    }

    [Theory]
    [InlineData("srv01:3390", "srv01", 3390)]
    [InlineData("[fe80::1]:3390", "fe80::1", 3390)]
    [InlineData("fe80::1", "fe80::1", null)]
    [InlineData("srv01", "srv01", null)]
    public void SplitsRdpAddresses(string address, string host, int? port) =>
        Assert.Equal((host, port), ImportedSession.SplitPort(address));

    [Fact]
    public void RefusesOversizedFiles()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("zt-big-").FullName, "big.reg");
        try
        {
            using (var stream = File.Create(path))
            {
                stream.SetLength((ImportText.MaxMegabytes * 1024L * 1024L) + 1);
            }

            Assert.Throws<InvalidDataException>(() => SessionSources.Read(ImportSourceKind.RegFile, path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    private static string Write(string name, string text, Encoding encoding)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("zt-import-").FullName, name);
        File.WriteAllText(path, text, encoding);
        return path;
    }
}
