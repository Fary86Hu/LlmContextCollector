namespace LlmContextCollector.Models
{
    public class CommitAndPushArgs
    {
        public string BranchName { get; }
        public string CommitMessage { get; }
        public List<DiffResult> AcceptedFiles { get; }

        public CommitAndPushArgs(string branchName, string commitMessage, List<DiffResult> acceptedFiles)
        {
            BranchName = branchName;
            CommitMessage = commitMessage;
            AcceptedFiles = acceptedFiles;
        }
    }
}