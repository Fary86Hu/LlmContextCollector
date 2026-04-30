using System;
using System.Diagnostics;
using System.Text;

namespace LlmContextCollector.Services
{
    public class GitService
    {
        private readonly AppState _appState;

        public GitService(AppState appState)
        {
            _appState = appState;
        }

        public async Task<(bool success, string output, string error)> RunGitCommandAsync(IEnumerable<string> arguments, bool throwOnError = false)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = _appState.ProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            foreach (var arg in arguments)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (throwOnError && process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git command failed with exit code {process.ExitCode}:\n{error}");
            }

            return (process.ExitCode == 0, output, error);
        }

        public async Task<(string branchName, bool success, string error)> GetCurrentBranchAsync()
        {
            var (success, output, error) = await RunGitCommandAsync(new[] { "rev-parse", "--abbrev-ref", "HEAD" });
            return (output.Trim(), success, error);
        }

        public async Task CreateAndCheckoutBranchAsync(string branchName)
        {
            await RunGitCommandAsync(new[] { "checkout", "-b", branchName }, throwOnError: true);
        }

        public async Task StageFilesAsync(IEnumerable<string> filePaths)
        {
            foreach (var path in filePaths)
            {
                await RunGitCommandAsync(new[] { "add", path }, throwOnError: true);
            }
        }

        public async Task CommitAsync(string message)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "commit -F -",
                    WorkingDirectory = _appState.ProjectRoot,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardInputEncoding = Encoding.UTF8
                }
            };

            process.Start();
            await process.StandardInput.WriteAsync(message);
            process.StandardInput.Close(); 

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git commit failed:\n{error}");
            }
        }

        public async Task PushAsync(string branchName)
        {
            if (!string.IsNullOrWhiteSpace(_appState.GitPersonalAccessToken))
            {
                var (success, output, _) = await RunGitCommandAsync(new[] { "remote", "get-url", "origin" });
                if (success)
                {
                    var url = output.Trim();
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                    {
                        var builder = new UriBuilder(uri) { UserName = _appState.GitPersonalAccessToken };
                        var authUrl = builder.ToString();

                        // Push művelet a tokenes URL használatával, de az upstream-et nem állítjuk át 
                        // a tokenes verzióra a config-ban a biztonság megőrzése érdekében.
                        await RunGitCommandAsync(new[] { "push", authUrl, $"HEAD:{branchName}" }, throwOnError: true);
                        return;
                    }
                }
            }

            await RunGitCommandAsync(new[] { "push", "--set-upstream", "origin", branchName }, throwOnError: true);
        }

        public async Task<bool> FileExistsInRefAsync(string gitRef, string filePath)
        {
            var (success, _, _) = await RunGitCommandAsync(new[] { "cat-file", "-e", $"{gitRef}:{filePath}" });
            return success;
        }

        public async Task<string> GetFileContentAtBranchAsync(string branch, string filePath)
        {
            var (success, output, error) = await RunGitCommandAsync(new[] { "show", $"{branch}:{filePath}" });
            if (!success) return $"[HIBA: Nem sikerült beolvasni az eredeti verziót ({branch}). Hiba: {error}]";
            return output;
        }

        public async Task DiscardChangesAsync(string filePath, string source = "HEAD")
        {
            await RunGitCommandAsync(new[] { "restore", $"--source={source}", "--staged", "--worktree", filePath }, throwOnError: true);
        }
    }
}