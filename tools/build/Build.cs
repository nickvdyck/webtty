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
using System.Runtime.InteropServices;
using System.Linq;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(build => build.Default);

    [PathExecutable("yarn")]
    private readonly Tool Yarn;

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution]
    private readonly Solution Solution;

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
            var version = DotNet("nbgv get-version -v NuGetPackageVersion").FirstOrDefault();

            DotNetToolInstall(s => s
                .AddSources(ArtifactsDirectory)
                .SetGlobal(true)
                .SetVersion(version.Text)
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

    Target TestJS => _ => _
        .Executes(() =>
        {
            Yarn("test", workingDirectory: Solution.GetProject(UI_PROJECT).Directory / "Client");
        });

    Target Test => _ => _
        .Triggers(TestJS)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(Solution.GetProject("WebTty.Test"))
                .When(!IsLocalBuild, w =>
                    w.SetNoBuild(true)
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
            Parallel.Run(
                $"dotnet nuke {nameof(WatchUI)}",
                $"dotnet nuke {nameof(WatchServer)}"
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

            DotNetPack(s => s
                .SetProject(Solution.GetProject(CLI_PROJECT))
                .SetConfiguration(Configuration)
                .SetOutputDirectory(ArtifactsDirectory)
                .SetProperty("IsPackaging", true));

            DotNetPublish(s => s
                .SetProject(Solution.GetProject(CLI_PROJECT))
                .SetConfiguration(Configuration)
                .SetRuntime("osx-x64")
                .SetOutput(ArtifactsDirectory / "osx")
                .SetProperty("PublishTrimmed", true));
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
}
