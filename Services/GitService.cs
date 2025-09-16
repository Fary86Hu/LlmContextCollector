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

        public async Task<(bool success, string output, string error)> RunGitCommandAsync(string arguments, bool throwOnError = false)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = _appState.ProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

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
            var (success, output, error) = await RunGitCommandAsync("rev-parse --abbrev-ref HEAD");
            return (output.Trim(), success, error);
        }

        public async Task CreateAndCheckoutBranchAsync(string branchName)
        {
            await RunGitCommandAsync($"checkout -b {branchName}", throwOnError: true);
        }

        public async Task StageFilesAsync(IEnumerable<string> filePaths)
        {
            foreach (var path in filePaths)
            {
                await RunGitCommandAsync($"add \"{path}\"", throwOnError: true);
            }
        }

        public async Task CommitAsync(string message)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "commit -F -", // Read message from stdin
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
            await RunGitCommandAsync($"push --set-upstream origin {branchName}", throwOnError: true);
        }
    }
}