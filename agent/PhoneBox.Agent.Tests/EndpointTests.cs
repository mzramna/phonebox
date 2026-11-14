using PhoneBox.Agent;
using Xunit;

public sealed class EndpointTests
{
    [Fact]
    public void DefaultsAndIsolatedInstancesHaveDistinctUrlsAndMutexes()
    {
        var standard = AgentEndpoint.FromArgs([]);
        var isolated = AgentEndpoint.FromArgs(["--config", "test.json", "--port", "18765"]);
        Assert.Equal(8765, standard.Port);
        Assert.Equal(@"Local\PhoneBox.Agent", standard.MutexName);
        Assert.Equal("http://127.0.0.1:18765/", isolated.SettingsUrl);
        Assert.NotEqual(standard.MutexName, isolated.MutexName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    [InlineData("8765; command")]
    public void InvalidPortsAreRejected(string value) => Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port", value]));

    [Fact]
    public void MissingAndRepeatedPortsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port"]));
        Assert.Throws<ArgumentException>(() => AgentEndpoint.FromArgs(["--port", "18765", "--port", "18766"]));
    }

    [Fact]
    public void RestartPreservesTheConfigPathAndPortAsSeparateArguments()
    {
        var info = AppRestart.StartInfo("PhoneBoxAgent.exe", ["--config", "C:/test folder/config.json", "--port", "18765", "--restart"]);
        Assert.Equal(new[] { "--config", "C:/test folder/config.json", "--port", "18765", "--restart" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        var hosted = AppRestart.StartInfo("dotnet", ["--port", "18765"]);
        Assert.EndsWith("PhoneBoxAgent.dll", hosted.ArgumentList[0]);
    }

    [Fact]
    public void InsideAnAppImageTheRestartStartsTheAppImageFile()
    {
        var file = Path.GetTempFileName();
        const string mount = "/tmp/.mount_PhoneBoxAbc", inside = mount + "/usr/lib/phonebox/PhoneBoxAgent", installed = "/opt/phonebox/PhoneBoxAgent";
        try
        {
            // running from the AppImage: start the .AppImage file again
            Assert.Equal(file, AppRestart.Executable(inside, file, mount, linux: true));
            Assert.Equal(file, AppRestart.Executable(inside, file, mount + "/", linux: true));
            // APPIMAGE / APPDIR inherited from another AppImage app (e.g. its terminal): restart this agent itself
            Assert.Equal(installed, AppRestart.Executable(installed, file, mount, linux: true));
            Assert.Equal(installed, AppRestart.Executable(installed, file, "/tmp/.mount_Other", linux: true));
            Assert.Equal("/tmp/.mount_PhoneBoxAbcdef/PhoneBoxAgent", AppRestart.Executable("/tmp/.mount_PhoneBoxAbcdef/PhoneBoxAgent", file, mount, linux: true));
            // not Linux, no APPDIR, no APPIMAGE or a missing file: never
            Assert.Equal(inside, AppRestart.Executable(inside, file, mount, linux: false));
            Assert.Equal(inside, AppRestart.Executable(inside, file, "", linux: true));
            Assert.Equal(inside, AppRestart.Executable(inside, "", mount, linux: true));
            Assert.Equal(inside, AppRestart.Executable(inside, "/no/such/file.AppImage", mount, linux: true));
        }
        finally { File.Delete(file); }
    }
}
