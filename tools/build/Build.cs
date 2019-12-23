using System;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using System.Runtime.InteropServices;
using System.Linq;
using System.IO;
using System.Xml.Linq;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(build => build.Default);

    [PathExecutable("yarn")]
    readonly Tool Yarn;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution] readonly Solution Solution;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "test";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath BuildOutputDirectory => RootDirectory / ".build";

    private const string CLI_PROJECT = "WebTty";
    private const string EXEC_PROJECT = "WebTty.Exec";
    private const string UI_PROJECT = "WebTty.UI";

    readonly string CliToolName = "webtty";

    Target Default => _ => _
        .Executes(() => Configuration = Configuration.Release)
        .Triggers(Setup)
        .Triggers(Package);

    Target Install => _ => _
        .Executes(() =>
        {
            var buildNumber = GitRevListHeadCount();
            var versionSuffix = $"build.{buildNumber}";

            var fileName = "Version.props";
            var currentDirectory = Directory.GetCurrentDirectory();
            var versionPropsFilePath = Path.Combine(currentDirectory, fileName);

            var versionProps = XElement.Load(versionPropsFilePath);
            var query = from props in versionProps.Elements("PropertyGroup")
                        from v in props.Elements("VersionPrefix")
                        select v.Value;

            var version = query.ToList().FirstOrDefault();

            DotNetToolInstall(s => s
                .AddSources(ArtifactsDirectory)
                .SetGlobal(true)
                .SetVersion($"{version}-{versionSuffix}")
                .SetPackageName(CliToolName));
        });

    Target UnInstall => _ => _
        .Executes(() =>
        {
            DotNetToolUninstall(s => s
                .SetGlobal(true)
                .SetPackageName(CliToolName));
        });

    Target Setup => _ => _
        .Executes(() =>
        {
            DotNetRun(s => s
                .SetProjectFile(Solution.GetProject("jsonschema")));

            var useFrozenLockfile = IsLocalBuild ? "" : " --frozen-lockfile";
            Yarn(
                $"install{useFrozenLockfile}",
                workingDirectory: Solution.GetProject(UI_PROJECT).Directory / "Client"
            );
        })
        .Triggers(Restore);

    Target Clean => _ => _
        .Executes(() =>
        {
            Yarn($"run clean", workingDirectory: Solution.GetProject(UI_PROJECT).Directory / "Client");
            EnsureCleanDirectory(ArtifactsDirectory);
            EnsureCleanDirectory(Solution.GetProject(UI_PROJECT).Directory / "wwwroot");
        });

    Target Purge => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {

            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            DeleteDirectory(BuildOutputDirectory / "bin");
            DeleteDirectory(BuildOutputDirectory / "obj");
            DeleteDirectory(BuildOutputDirectory / "tools");
            DeleteDirectory(ArtifactsDirectory);
            DeleteDirectory(Solution.GetProject(UI_PROJECT).Directory / "Client"/ "node_modules");
        });

    Target Check => _ => _
        .DependsOn(Setup)
        .Triggers(Lint)
        .Triggers(CheckTypes);

    Target Lint => _ => _
        .Executes(() => {
            Yarn($"run lint", workingDirectory: Solution.GetProject(UI_PROJECT).Directory / "Client");
        });

    Target CheckTypes => _ => _
        .Executes(() => {
            Yarn($"tsc --noEmit", workingDirectory: Solution.GetProject(UI_PROJECT).Directory / "Client");
        });

    Target Test => _ => _
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(Solution.GetProject("WebTty.Test"))
                .When(!IsLocalBuild, s =>
                    s.SetNoBuild(true)
                    .SetResultsDirectory(ArtifactsDirectory / "TestResults")
                    .SetLogger("trx")
                )
            );

            // Temporary removed until I have a better plan to run integration tests in ci
            // DotNetTest(s => s
            //     .SetProjectFile(Solution.GetProject("WebTty.Integration.Test"))
            //     .SetNoBuild(true)
            //     .SetResultsDirectory(ArtifactsDirectory / "TestResults")
            //     .SetLogger("trx"));
        });

    Target Watch => _ => _
        .DependsOn(Clean)
        .DependsOn(CompileUI)
        .Executes(() =>
        {
            var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ps1" : "sh";
            var usePwsh = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pwsh " : "";
            Parallel.Run(
                $"{usePwsh}./build.{ext} {nameof(WatchUI)} --no-logo ",
                $"{usePwsh}./build.{ext} {nameof(WatchServer)} --no-logo"
            );
        });

    Target WatchUI => _ => _
        .Executes(() =>
        {
            Yarn("run watch", workingDirectory: Solution.GetProject(UI_PROJECT).Directory / "Client");
        });

    Target WatchServer => _ => _
        .Executes(() =>
        {
            DotNet("watch run", workingDirectory: Solution.GetProject(CLI_PROJECT).Directory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore, CompileUI)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target CompileUI => _ => _
        .Executes(() =>
        {
            Yarn($"run build", workingDirectory: Solution.GetProject(UI_PROJECT).Directory / "Client");
        });

    Target Package => _ => _
        .DependsOn(Clean)
        .DependsOn(Restore)
        .DependsOn(CompileUI)
        .DependsOn(PackageNative)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetForceEvaluate(true));

            var buildNumber = GitRevListHeadCount();
            var sourceRevisionId = GitRevParseHead();

            DotNetPack(s => s
                .SetProject(Solution.GetProject(CLI_PROJECT))
                .SetConfiguration(Configuration)
                .SetOutputDirectory(ArtifactsDirectory)
                .SetVersionSuffix($"build.{buildNumber}")
                .SetProperty("SourceRevisionId", sourceRevisionId)
                .SetProperty("IsPackaging", true));
        });

    Target PackageNative => _ => _
        .DependsOn(Clean)
        .DependsOn(Restore)
        .Executes(() =>
        {
            var suffix = $"build.{DateTime.Now.ToString("yyyyMMddHHmmss")}";

            DotNetPack(s => s
                .SetProject(Solution.GetProject(EXEC_PROJECT))
                .SetConfiguration(Configuration)
                .SetOutputDirectory(ArtifactsDirectory)
                .SetVersionSuffix(suffix));
        });

    public static string GitRevListHeadCount() =>
        Git("rev-list --count HEAD").FirstOrDefault().Text.Trim();

    public static string GitRevParseHead() =>
        Git("rev-parse HEAD").FirstOrDefault().Text.Trim();

}
