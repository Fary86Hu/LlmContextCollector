using LlmContextCollector.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System;

namespace LlmContextCollector.Services
{
    public class AppState : INotifyPropertyChanged
    {
        private readonly PromptService _promptService;

        public AppState(PromptService promptService)
        {
            _promptService = promptService;
        }

        private string _projectRoot = string.Empty;
        public string ProjectRoot
        {
            get => _projectRoot;
            set => SetField(ref _projectRoot, value);
        }

        private bool _isGitRepository = false;
        public bool IsGitRepository
        {
            get => _isGitRepository;
            set => SetField(ref _isGitRepository, value);
        }

        private string _currentGitBranch = string.Empty;
        public string CurrentGitBranch
        {
            get => _currentGitBranch;
            set => SetField(ref _currentGitBranch, value);
        }

        private List<FileNode> _fileTree = new();
        public List<FileNode> FileTree => _fileTree;
        public ObservableCollection<string> SelectedFilesForContext { get; } = new();

        public Dictionary<string, bool> ExtensionFilters { get; } = new()
        {
            { ".razor", true }, { ".cs", true }, { ".js", true }, { ".css", true },
            { ".html", true }, { ".cshtml", true }, { ".json", true }, { ".xml", true },
            { ".txt", true }, { ".md", true }
        };

        private Dictionary<string, int> _extensionCounts = new();
        public Dictionary<string, int> ExtensionCounts
        {
            get => _extensionCounts;
            set => SetField(ref _extensionCounts, value);
        }

        private string _ignorePatternsRaw = string.Join("\n", new[]
        {
            "node_modules", "vendor", "dist", "build", "target", "__pycache__",
            "bin", "obj", ".git", ".svn", ".hg", ".idea", ".vscode",
            "*.pyc", "*.pyo", "*.class", "*.o", "*.obj", "*.dll", "*.so", "*.exe", ".DS_Store"
        });
        public string IgnorePatternsRaw
        {
            get => _ignorePatternsRaw;
            set
            {
                if (SetField(ref _ignorePatternsRaw, value))
                {
                    SyncExclusionsFromRaw();
                }
            }
        }

        public ObservableCollection<ExclusionRule> Exclusions { get; } = new();

        private void SyncExclusionsFromRaw()
        {
            var lines = _ignorePatternsRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var currentPatterns = Exclusions.ToDictionary(e => e.Pattern);
            
            var newRules = new List<ExclusionRule>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                bool isEnabled = !trimmed.StartsWith("#");
                string pattern = isEnabled ? trimmed : trimmed.Substring(1).Trim();

                newRules.Add(new ExclusionRule { Pattern = pattern, IsEnabled = isEnabled });
            }

            Exclusions.Clear();
            foreach (var rule in newRules) Exclusions.Add(rule);
        }

        public void UpdateRawFromExclusions()
        {
            var lines = Exclusions.Select(e => (e.IsEnabled ? "" : "# ") + e.Pattern.Trim());
            _ignorePatternsRaw = string.Join("\n", lines);
            NotifyStateChanged(nameof(IgnorePatternsRaw));
        }

        private string _searchTerm = string.Empty;
        public string SearchTerm
        {
            get => _searchTerm;
            set => SetField(ref _searchTerm, value);
        }

        private bool _searchInContent = false;
        public bool SearchInContent
        {
            get => _searchInContent;
            set => SetField(ref _searchInContent, value);
        }

        private int _referenceSearchDepth = 1;
        public int ReferenceSearchDepth
        {
            get => _referenceSearchDepth;
            set => SetField(ref _referenceSearchDepth, value);
        }

        private bool _includeReferencingFiles = false;
        public bool IncludeReferencingFiles
        {
            get => _includeReferencingFiles;
            set => SetField(ref _includeReferencingFiles, value);
        }

        private string _promptText = string.Empty;
        public string PromptText
        {
            get => _promptText;
            set => SetField(ref _promptText, value);
        }

        private string _theme = "System";
        public string Theme
        {
            get => _theme;
            set => SetField(ref _theme, value);
        }

        private Guid _activeGlobalPromptId;
        public Guid ActiveGlobalPromptId
        {
            get => _activeGlobalPromptId;
            set
            {
                if (SetField(ref _activeGlobalPromptId, value))
                {
                    _ = _promptService.SetActivePromptIdAsync(value);
                }
            }
        }
        
        public List<PromptTemplate> PromptTemplates { get; set; } = new();
        
        public async Task LoadPromptsAsync()
        {
            PromptTemplates = await _promptService.GetPromptsAsync();
            var activeId = await _promptService.GetActivePromptIdAsync();
            _activeGlobalPromptId = activeId;
            
            NotifyStateChanged(nameof(PromptTemplates));
            NotifyStateChanged(nameof(ActiveGlobalPromptId));
        }

        private string _groqApiKey = string.Empty;
        public string GroqApiKey
        {
            get => _groqApiKey;
            set => SetField(ref _groqApiKey, value);
        }

        private string _groqModel = "llama-3.3-70b-versatile";
        public string GroqModel
        {
            get => _groqModel;
            set => SetField(ref _groqModel, value);
        }

        private int _groqMaxOutputTokens = 2048;
        public int GroqMaxOutputTokens
        {
            get => _groqMaxOutputTokens;
            set => SetField(ref _groqMaxOutputTokens, value);
        }
        
        private string _groqApiUrl = "https://api.groq.com/openai/v1/";
        public string GroqApiUrl
        {
            get => _groqApiUrl;
            set => SetField(ref _groqApiUrl, value);
        }

        private string _ollamaApiUrl = "http://localhost:11434/v1/";
        public string OllamaApiUrl
        {
            get => _ollamaApiUrl;
            set => SetField(ref _ollamaApiUrl, value);
        }

        private string _ollamaModel = "qwen3:4b-instruct";
        public string OllamaModel
        {
            get => _ollamaModel;
            set => SetField(ref _ollamaModel, value);
        }

        private bool _useOllamaEmbeddings = false;
        public bool UseOllamaEmbeddings
        {
            get => _useOllamaEmbeddings;
            set => SetField(ref _useOllamaEmbeddings, value);
        }

        private string _ollamaEmbeddingModel = "nomic-embed-text";
        public string OllamaEmbeddingModel
        {
            get => _ollamaEmbeddingModel;
            set => SetField(ref _ollamaEmbeddingModel, value);
        }

        public List<HistoryEntry> HistoryEntries { get; set; } = new();

        private string _azureDevOpsOrganizationUrl = "";
        public string AzureDevOpsOrganizationUrl { get => _azureDevOpsOrganizationUrl; set => SetField(ref _azureDevOpsOrganizationUrl, value); }

        private string _azureDevOpsProject = "";
        public string AzureDevOpsProject { get => _azureDevOpsProject; set => SetField(ref _azureDevOpsProject, value); }

        private string _azureDevOpsRepository = "";
        public string AzureDevOpsRepository { get => _azureDevOpsRepository; set => SetField(ref _azureDevOpsRepository, value); }

        private string _azureDevOpsIterationPath = "";
        public string AzureDevOpsIterationPath { get => _azureDevOpsIterationPath; set => SetField(ref _azureDevOpsIterationPath, value); }

        private string _azureDevOpsPat = "";
        public string AzureDevOpsPat { get => _azureDevOpsPat; set => SetField(ref _azureDevOpsPat, value); }

        private string _adoDocsPath = string.Empty;
        public string AdoDocsPath { get => _adoDocsPath; set => SetField(ref _adoDocsPath, value); }
        private bool _adoDocsExist = false;
        public bool AdoDocsExist { get => _adoDocsExist; set => SetField(ref _adoDocsExist, value); }
        
        private DateTime? _adoLastDownloadDate;
        public DateTime? AdoLastDownloadDate { get => _adoLastDownloadDate; set => SetField(ref _adoLastDownloadDate, value); }

        private bool _adoDownloadOnlyMine = false;
        public bool AdoDownloadOnlyMine { get => _adoDownloadOnlyMine; set => SetField(ref _adoDownloadOnlyMine, value); }

        public string LastLlmGlobalExplanation { get; set; } = string.Empty;

        private bool _isDiffDialogVisible = false;
        public bool IsDiffDialogVisible
        {
            get => _isDiffDialogVisible;
            set => SetField(ref _isDiffDialogVisible, value);
        }
        public string DiffGlobalExplanation { get; set; } = string.Empty;
        public string DiffFullLlmResponse { get; set; } = string.Empty;
        public List<DiffResult> DiffResults { get; set; } = new();

        private string _statusText = "Készen áll.";
        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        private bool _isLoading = false;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        private bool _isSemanticIndexBuilding = false;
        public bool IsSemanticIndexBuilding
        {
            get => _isSemanticIndexBuilding;
            set => SetField(ref _isSemanticIndexBuilding, value);
        }

        private bool _isAiInspectorVisible = false;
        public bool IsAiInspectorVisible
        {
            get => _isAiInspectorVisible;
            set => SetField(ref _isAiInspectorVisible, value);
        }

        private string _loadingText = string.Empty;
        public string LoadingText
        {
            get => _loadingText;
            set => SetField(ref _loadingText, value);
        }

        private List<List<string>> _contextListHistory = new();
        private int _contextListHistoryIndex = -1;
        public bool CanUndo => _contextListHistoryIndex > 0;
        public bool CanRedo => _contextListHistoryIndex < _contextListHistory.Count - 1;

        private double _leftPanelFlex = 30;
        public double LeftPanelFlex { get => _leftPanelFlex; set => SetField(ref _leftPanelFlex, value); }

        private double _middlePanelFlex = 20;
        public double MiddlePanelFlex { get => _middlePanelFlex; set => SetField(ref _middlePanelFlex, value); }

        private double _rightPanelFlex = 50;
        public double RightPanelFlex { get => _rightPanelFlex; set => SetField(ref _rightPanelFlex, value); }

        private double _rightTopPanelFlex = 30;
        public double RightTopPanelFlex { get => _rightTopPanelFlex; set => SetField(ref _rightTopPanelFlex, value); }

        private double _rightMiddlePanelFlex = 40;
        public double RightMiddlePanelFlex { get => _rightMiddlePanelFlex; set => SetField(ref _rightMiddlePanelFlex, value); }

        private double _rightBottomPanelFlex = 30;
        public double RightBottomPanelFlex { get => _rightBottomPanelFlex; set => SetField(ref _rightBottomPanelFlex, value); }

        public void SaveContextListState()
        {
            var currentState = SelectedFilesForContext.ToList();
            if (_contextListHistoryIndex < _contextListHistory.Count - 1)
            {
                _contextListHistory.RemoveRange(_contextListHistoryIndex + 1, _contextListHistory.Count - (_contextListHistoryIndex + 1));
            }
            
            if (!_contextListHistory.Any() || !_contextListHistory.Last().SequenceEqual(currentState))
            {
                _contextListHistory.Add(currentState);
                _contextListHistoryIndex = _contextListHistory.Count - 1;
            }
            NotifyStateChanged(nameof(CanUndo));
            NotifyStateChanged(nameof(CanRedo));
        }

        public void UndoContextListChange()
        {
            if (!CanUndo) return;
            _contextListHistoryIndex--;
            RestoreContextListFromHistory();
            StatusText = "Visszavonva.";
        }

        public void RedoContextListChange()
        {
            if (!CanRedo) return;
            _contextListHistoryIndex++;
            RestoreContextListFromHistory();
            StatusText = "Ismételve.";
        }

        public void ResetContextListHistory()
        {
            _contextListHistory.Clear();
            _contextListHistory.Add(new List<string>());
            _contextListHistoryIndex = 0;
        }

        private void RestoreContextListFromHistory()
        {
            var stateToRestore = _contextListHistory[_contextListHistoryIndex];
            SelectedFilesForContext.Clear();
            foreach (var item in stateToRestore)
            {
                SelectedFilesForContext.Add(item);
            }
            NotifyStateChanged(nameof(CanUndo));
            NotifyStateChanged(nameof(CanRedo));
            NotifyStateChanged(nameof(SelectedFilesForContext));
        }

        public void ShowLoading(string text)
        {
            LoadingText = text;
            IsLoading = true;
        }

        public void HideLoading()
        {
            IsLoading = false;
            LoadingText = string.Empty;
        }

        public void SetFileTree(List<FileNode> tree)
        {
            _fileTree = tree;
            NotifyStateChanged(nameof(FileTree));
        }

        public void AddExtensionFilter(string extension)
        {
            if (!ExtensionFilters.ContainsKey(extension))
            {
                ExtensionFilters[extension] = true;
                NotifyStateChanged(nameof(ExtensionFilters));
            }
        }
        
        public FileNode? FindNodeByPath(string fullPath)
        {
            FileNode? Find(IEnumerable<FileNode> nodes)
            {
                foreach (var node in nodes)
                {
                    if (node.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase)) return node;
                    if (node.IsDirectory)
                    {
                        var found = Find(node.Children);
                        if (found != null) return found;
                    }
                }
                return null;
            }
            return Find(this.FileTree);
        }

        public void ExpandNodeParents(FileNode? node)
        {
            if (node?.Parent == null) return;
            var parent = node.Parent;
            while (parent != null)
            {
                parent.IsExpanded = true;
                parent = parent.Parent;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void NotifyStateChanged([CallerMemberName] string? propertyName = null)
        {
            OnPropertyChanged(propertyName);
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}