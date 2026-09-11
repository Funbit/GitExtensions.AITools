using System.Diagnostics;
using System.Text;
using GitExtensions.AITools;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

internal static partial class Program
{
    private static void StagedAssetsAreExcluded()
    {
        using GitRepositoryFixture repository = new();
        string[] excluded =
        [
            "wwwroot/sc/assets/root-bundle.js",
            "hg/ff/Fileforce.SecurityCenter.Server/wwwroot/sc/assets/security-bundle.js",
            "hg/ff/Fileforce.TeamsIntegration.Server/wwwroot/teams/assets/chunks/team-bundle.js",
            "project with spaces/wwwroot/app/assets/icons/menu icon.svg",
        ];
        string[] included =
        [
            "src/Service.cs",
            "hg/ff/Fileforce.SecurityCenter.Server/wwwroot/sc/index.html",
            "wwwroot/sc/assets-other/keep.js",
            "wwwroot/assets/keep.js",
            "wwwroot/sc/nested/assets/keep.js",
            "other-wwwroot/sc/assets/keep.js",
        ];
        foreach (string path in excluded) repository.WriteFile(path, "EXCLUDED_ASSET_CONTENT");
        foreach (string path in included) repository.WriteFile(path, "INCLUDED_SOURCE_CONTENT");
        repository.RunGit("add --all");
        string stagedBefore = repository.RunGit("diff --cached --name-only");

        // Also collect from below the repository root to exercise root-relative exclusions.
        foreach (string directory in new[] { "", "hg/ff/Fileforce.SecurityCenter.Server" })
        {
            repository.WorkingDirectory = Path.Combine(repository.Root, directory);
            string analysis = DiffCollector.GetStagedDiffAsync(repository.Module, CancellationToken.None).GetAwaiter().GetResult();
            int patchStart = analysis.IndexOf("diff --git ", StringComparison.Ordinal);
            Check(patchStart >= 0, "Source changes must still produce a patch.");
            string summary = analysis[..patchStart];
            string patch = analysis[patchStart..];
            Check(summary.Contains($"{included.Length} files changed"), "The summary must count only included files.");
            Check(!analysis.Contains("EXCLUDED_ASSET_CONTENT"), "Asset contents must not reach the AI analysis.");
            foreach (string path in excluded)
            {
                Check(!analysis.Contains(Path.GetFileName(path)), $"Asset filename leaked into the summary or patch: {path}");
            }
            foreach (string path in included)
            {
                Check(patch.Contains(path), $"An unrelated staged file was excluded: {path}");
            }
        }

        repository.WorkingDirectory = repository.Root;
        Check(repository.RunGit("diff --cached --name-only") == stagedBefore, "Collecting the analysis must preserve all staged files.");
    }

    private static void AssetOnlyChangesSkipAi()
    {
        using GitRepositoryFixture repository = new();
        repository.WriteFile("wwwroot/sc/assets/bundle.js", "generated asset");
        repository.WriteFile("hg/ff/Fileforce.TeamsIntegration.Server/wwwroot/teams/assets/nested/bundle.js", "generated asset");
        repository.RunGit("add --all");
        ControlledProvider provider = new();
        CommitMessageGenerator generator = new(provider, CommitMessageGenerator.DefaultCommitTypes, null);
        Task<string> generation = generator.GenerateAsync(repository.Module, CancellationToken.None);
        // Resolve an unexpected request so a filtering regression fails without hanging.
        foreach (ControlledProvider.Request request in provider.Requests) request.Result.TrySetResult("Unexpected AI request");
        PumpUntil(() => generation.IsCompleted);
        Check(provider.Requests.Length == 0, "The AI provider must not receive an asset-only change.");
        Check(generation.GetAwaiter().GetResult() == CommitMessageGenerator.NoStagedChangesMessage,
            "No eligible changes must follow the existing empty-diff behavior.");
    }

    private sealed class GitRepositoryFixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("GitExtensions.AITools.Tests-");
        public string Root => _directory.FullName;
        public string WorkingDirectory { get; set; }
        public IGitModule Module { get; }

        public GitRepositoryFixture()
        {
            WorkingDirectory = Root;
            RunGit("init --quiet");
            IExecutable executable = Stub<IExecutable>((method, args) =>
            {
                if (method.Name != "Start") throw new NotSupportedException(method.Name);
                string output = RunGit(args![0]!.ToString()!);
                StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(output)));
                return Stub<IProcess>((member, _) => member.Name switch
                {
                    "get_StandardOutput" => reader,
                    "WaitForExitAsync" => Task.FromResult(0),
                    "Dispose" => DisposeReader(reader),
                    _ => throw new NotSupportedException(member.Name),
                });
            });
            Module = Stub<IGitModule>((method, _) => method.Name switch
            {
                "get_GitExecutable" => executable,
                "GetSelectedBranch" => "test-branch",
                _ => throw new NotSupportedException(method.Name),
            });
        }

        public void WriteFile(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content + "\n");
        }

        public string RunGit(string arguments)
        {
            ProcessStartInfo start = new("git", "-c core.autocrlf=false -c core.quotePath=false " + arguments)
            {
                WorkingDirectory = WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                start.Environment.Remove(key);
            }
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Root, "unused.gitconfig");
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"git {arguments} did not finish.");
            }
            Check(process.ExitCode == 0, $"git {arguments} failed: {error.GetAwaiter().GetResult()}");
            return output.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            Check(_directory.Parent!.FullName == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                "The test repository must stay inside the temporary directory.");
            foreach (FileInfo file in _directory.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
            _directory.Delete(recursive: true);
        }

        private static object? DisposeReader(StreamReader reader)
        {
            reader.Dispose();
            return null;
        }
    }
}
