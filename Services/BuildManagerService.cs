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
        private Process? _activeProcess;

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
            var workingDir = DetermineWorkingDirectory(_appState.DefaultBuildCommand);
            await ExecuteProcessAsync(_appState.DefaultBuildCommand, "Build", parseErrors: true, workingDir);
        }

        public async Task RunProjectAsync()
        {
            var command = _appState.DefaultRunCommand;
            
            // Hot Reload (dotnet watch) támogatás
            if (_appState.UseHotReload && command.StartsWith("dotnet run"))
            {
                command = "dotnet watch " + command.Substring(7); // 'dotnet run' -> 'dotnet watch run'
            }

            var workingDir = DetermineWorkingDirectory(command);

            if (!string.IsNullOrEmpty(_appState.SelectedLaunchProfile) && 
                (command.StartsWith("dotnet run") || command.Contains("watch run")) && 
                !command.Contains("--launch-profile"))
            {
                command += $" --launch-profile \"{_appState.SelectedLaunchProfile}\"";
            }
            
            await ExecuteProcessAsync(command, "Run", parseErrors: false, workingDir);
        }

        private string DetermineWorkingDirectory(string command)
        {
            if (string.IsNullOrEmpty(_appState.ProjectRoot)) return string.Empty;

            // Megpróbáljuk kitalálni a .csproj mappáját a parancsból
            var csprojMatch = Regex.Match(command, @"(?<path>[\w\.\/\\]+\.csproj)");
            if (csprojMatch.Success)
            {
                var relPath = csprojMatch.Groups["path"].Value;
                var fullPath = Path.IsPathRooted(relPath) ? relPath : Path.Combine(_appState.ProjectRoot, relPath);
                var dir = Path.GetDirectoryName(fullPath);
                if (Directory.Exists(dir)) return dir;
            }

            return _appState.ProjectRoot;
        }

        private async Task ExecuteProcessAsync(string command, string logSource, bool parseErrors, string? workingDir = null)
        {
            var targetWorkingDir = !string.IsNullOrEmpty(workingDir) ? workingDir : _appState.ProjectRoot;
            if (string.IsNullOrWhiteSpace(targetWorkingDir) || string.IsNullOrWhiteSpace(command)) return;

            // Ha a munkakönyvtár a projekt alkönyvtára, a parancsban szereplő útvonalat le kell egyszerűsíteni
            if (!string.IsNullOrEmpty(workingDir) && workingDir != _appState.ProjectRoot)
            {
                var csprojMatch = Regex.Match(command, @"(?<path>[\w\.\/\\]+\.csproj)");
                if (csprojMatch.Success)
                {
                    var fullPathInCommand = csprojMatch.Groups["path"].Value;
                    var fileNameOnly = Path.GetFileName(fullPathInCommand);
                    
                    // Kicseréljük a parancsban a relatív utat a sima fájlnévre
                    command = command.Replace(fullPathInCommand, fileNameOnly);
                }
            }

            CancelBuild();
            
            _buildCts = new CancellationTokenSource();
            var token = _buildCts.Token;

            _appState.CurrentBuildStatus = BuildStatus.Running;
            _appState.BuildOutput = string.Empty;
            _appState.CurrentBuildErrors.Clear();

            _logService.LogInfo(logSource, $"Indítás helye: {targetWorkingDir}");
            _logService.LogInfo(logSource, $"Parancs: {command}");

            try
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var fileName = parts[0];
                var arguments = string.Join(" ", parts.Skip(1));

                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = targetWorkingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _activeProcess = new Process { StartInfo = psi };
                var outputBuilder = new StringBuilder();

                _activeProcess.OutputDataReceived += (s, e) => ProcessLine(e.Data, outputBuilder, parseErrors);
                _activeProcess.ErrorDataReceived += (s, e) => ProcessLine(e.Data, outputBuilder, parseErrors);

                _activeProcess.Start();
                _activeProcess.BeginOutputReadLine();
                _activeProcess.BeginErrorReadLine();

                try
                {
                    await _activeProcess.WaitForExitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    if (_activeProcess != null && !_activeProcess.HasExited)
                    {
                        _activeProcess.Kill(true);
                    }
                    throw;
                }

                if (token.IsCancellationRequested)
                {
                    _appState.CurrentBuildStatus = BuildStatus.Idle;
                    _logService.LogWarning(logSource, "Folyamat megszakítva.");
                }
                else
                {
                    _appState.CurrentBuildStatus = _activeProcess.ExitCode == 0 ? BuildStatus.Success : BuildStatus.Failed;
                    _logService.LogInfo(logSource, $"Folyamat befejeződött (Exit code: {_activeProcess.ExitCode})");
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
            finally
            {
                _activeProcess?.Dispose();
                _activeProcess = null;
            }
        }

        public void CancelBuild()
        {
            _buildCts?.Cancel();
            
            try
            {
                if (_activeProcess != null && !_activeProcess.HasExited)
                {
                    _activeProcess.Kill(true);
                    _logService.LogWarning("BuildManager", "Folyamat kényszerítve leállítva.");
                }
            }
            catch (Exception ex)
            {
                _logService.LogWarning("BuildManager", $"Hiba a leállításkor: {ex.Message}");
            }
            finally
            {
                _activeProcess?.Dispose();
                _activeProcess = null;
                _buildCts?.Dispose();
                _buildCts = null;
            }
        }

        private void ProcessLine(string? line, StringBuilder fullOutput, bool parseErrors)
        {
            if (line == null) return;
            fullOutput.AppendLine(line);
            
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _appState.BuildOutput += line + Environment.NewLine;
                
                if (line.Contains("address already in use", StringComparison.OrdinalIgnoreCase) || 
                    line.Contains("Az összes szoftvercsatorna-cím használatának általában csak egy módja", StringComparison.OrdinalIgnoreCase))
                {
                    _appState.StatusText = "PORT HIBA: A hálózati port foglalt.";
                }

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