using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LlmContextCollector.Models;

namespace LlmContextCollector.Services
{
    public class BuildManagerService
    {
        private readonly AppState _appState;
        private readonly AppLogService _logService;
        private CancellationTokenSource? _buildCts;

        // MSBuild/dotnet error format: path(line,col): error CODE: Message
        private static readonly Regex MsBuildErrorRegex = new Regex(
            @"(?<path>.*)\((?<line>\d+),(?<col>\d+)\): error (?<code>\w+): (?<message>.*)",
            RegexOptions.Compiled);

        public BuildManagerService(AppState appState, AppLogService logService)
        {
            _appState = appState;
            _logService = logService;
        }

        public async Task RunBuildAsync()
        {
            await ExecuteProcessAsync(_appState.DefaultBuildCommand, "Build", parseErrors: true);
        }

        public async Task RunProjectAsync()
        {
            await ExecuteProcessAsync(_appState.DefaultRunCommand, "Run", parseErrors: false);
        }

        private async Task ExecuteProcessAsync(string command, string logSource, bool parseErrors)
        {
            if (string.IsNullOrWhiteSpace(_appState.ProjectRoot) || string.IsNullOrWhiteSpace(command)) return;

            CancelBuild();
            _buildCts = new CancellationTokenSource();
            var token = _buildCts.Token;

            _appState.CurrentBuildStatus = BuildStatus.Running;
            _appState.BuildOutput = string.Empty;
            _appState.CurrentBuildErrors.Clear();

            _logService.LogInfo(logSource, $"Folyamat indítása: {command}");

            try
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var fileName = parts[0];
                var arguments = string.Join(" ", parts.Skip(1));

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = _appState.ProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = psi };
                var outputBuilder = new StringBuilder();

                process.OutputDataReceived += (s, e) => ProcessLine(e.Data, outputBuilder, parseErrors);
                process.ErrorDataReceived += (s, e) => ProcessLine(e.Data, outputBuilder, parseErrors);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(token);

                if (token.IsCancellationRequested)
                {
                    if (!process.HasExited) process.Kill(true);
                    _appState.CurrentBuildStatus = BuildStatus.Idle;
                    _logService.LogWarning(logSource, "Folyamat megszakítva.");
                }
                else
                {
                    _appState.CurrentBuildStatus = process.ExitCode == 0 ? BuildStatus.Success : BuildStatus.Failed;
                    _logService.LogInfo(logSource, $"Folyamat befejeződött (Exit code: {process.ExitCode})");
                }
            }
            catch (OperationCanceledException)
            {
                _appState.CurrentBuildStatus = BuildStatus.Idle;
            }
            catch (Exception ex)
            {
                _appState.CurrentBuildStatus = BuildStatus.Failed;
                _appState.BuildOutput += $"\n[HIBA]: {ex.Message}";
                _logService.LogError(logSource, "Váratlan hiba a végrehajtás során", ex.Message);
            }
        }

        public void CancelBuild()
        {
            _buildCts?.Cancel();
            _buildCts?.Dispose();
            _buildCts = null;
        }

        private void ProcessLine(string? line, StringBuilder fullOutput, bool parseErrors)
        {
            if (line == null) return;

            fullOutput.AppendLine(line);
            
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _appState.BuildOutput = fullOutput.ToString();
                if (parseErrors)
                {
                    ParseAndAddError(line);
                }
            });
        }

        private void ParseAndAddError(string line)
        {
            var match = MsBuildErrorRegex.Match(line);
            if (match.Success)
            {
                var error = new BuildError
                {
                    FilePath = match.Groups["path"].Value.Trim(),
                    Line = int.Parse(match.Groups["line"].Value),
                    Column = int.Parse(match.Groups["col"].Value),
                    ErrorCode = match.Groups["code"].Value,
                    Message = match.Groups["message"].Value.Trim(),
                    Severity = "error"
                };
                _appState.CurrentBuildErrors.Add(error);
            }
        }
    }
}