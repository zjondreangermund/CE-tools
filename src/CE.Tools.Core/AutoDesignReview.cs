using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    /// <summary>Read-only review: displaying an item never approves or executes it.</summary>
    public static class AutoDesignReview
    {
        public static string AttentionText(IEnumerable<AutoDesignItem> plan, IEnumerable<string> issues)
        {
            var pending = plan.Where(x => x.Status != AutoDesignStatus.Completed && x.Status != AutoDesignStatus.Current).ToList();
            var findings = issues.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            var lines = new List<string>
            {
                "APPROVALS AND ATTENTION",
                "Approval decisions: " + pending.Count(x => x.Status == AutoDesignStatus.NeedsApproval) +
                "; failed: " + pending.Count(x => x.Status == AutoDesignStatus.Failed) +
                "; blocked: " + pending.Count(x => x.Status == AutoDesignStatus.Blocked) +
                "; validation findings: " + findings.Count,
                "Review the reasons below. Use the indicated CE command or settings to resolve each item, then rerun CE_AUTODESIGN. Closing this window does not approve changes.",
                ""
            };
            foreach (var item in pending.OrderBy(x => x.Status == AutoDesignStatus.NeedsApproval ? 0 : x.Status == AutoDesignStatus.Failed ? 1 : 2))
            {
                lines.Add(item.Status + ": " + item.Step.Description);
                lines.Add(string.IsNullOrWhiteSpace(item.Message) ? item.Step.ApprovalReason ?? "Rerun AutoDesign to recheck or resume this step." : item.Message);
                lines.Add("");
            }
            foreach (string finding in findings) { lines.Add("VALIDATION: " + finding); lines.Add(""); }
            if (pending.Count == 0 && findings.Count == 0) lines.Add("No outstanding automation items were reported.");
            lines.Add("Final engineering review remains required; V1 does not certify hydraulic capacity, cover/slope criteria, constructability or final sheets. See Full report for completed work and drawing inventory.");
            return string.Join(Environment.NewLine, lines);
        }
    }
}
