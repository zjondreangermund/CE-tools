using System;
using System.Collections.Generic;
using CETools.Core;

namespace CETools.Core.Tests
{
    internal static class AutoDesignReviewTests
    {
        public static int Run()
        {
            int executions = 0;
            var engine = new AutoDesignEngine();
            var checkpoints = new Dictionary<string, AutoDesignCheckpoint>();
            var plan = engine.Plan(new[]
            {
                new AutoDesignStep { Id = "approved", Description = "Place profiles", ApprovalReason = "Use CE_SEWPROFILE to choose placement.", Execute = () => executions++ },
                new AutoDesignStep { Id = "blocked", Description = "Validate profiles", Dependencies = new[] { "approved" } },
                new AutoDesignStep { Id = "done", Description = "Successful work", Execute = () => { } }
            }, AutoDesignMode.CompleteExistingDesign, "drawing", checkpoints);
            engine.Run(plan, AutoDesignMode.CompleteExistingDesign, checkpoints, () => "drawing", () => { }, (item, index, total) => { }, () => false);
            string text = AutoDesignReview.AttentionText(plan, new[] { "Check cover", "Check cover" });
            Check(text.Contains("Approval decisions: 1; failed: 0; blocked: 1; validation findings: 1"));
            Check(text.Contains("Use CE_SEWPROFILE to choose placement.") && text.Contains("Blocked: Validate profiles"));
            Check(!text.Contains("Successful work") && executions == 0 && plan[0].Status == AutoDesignStatus.NeedsApproval);
            var retry = engine.Plan(new[] { new AutoDesignStep { Id = "refresh", Description = "Refresh profiles", Execute = () => { throw new InvalidOperationException("Native refresh failed"); } } },
                AutoDesignMode.CompleteExistingDesign, "drawing", checkpoints);
            engine.Run(retry, AutoDesignMode.CompleteExistingDesign, checkpoints, () => "drawing", () => { }, (item, index, total) => { }, () => false);
            Check(AutoDesignReview.AttentionText(retry, new string[0]).Contains("Native refresh failed"));
            retry = engine.Plan(new[] { new AutoDesignStep { Id = "resume", Description = "Refresh profiles" } }, AutoDesignMode.CompleteExistingDesign, "drawing", checkpoints);
            engine.Run(retry, AutoDesignMode.CompleteExistingDesign, checkpoints, () => "drawing", () => { }, (item, index, total) => { }, () => true);
            Check(AutoDesignReview.AttentionText(retry, new string[0]).Contains("Cancelled: Refresh profiles"));
            Check(AutoDesignReview.AttentionText(new AutoDesignItem[0], new string[0]).Contains("No outstanding automation items"));
            return 6;
        }

        private static void Check(bool condition)
        {
            if (!condition) throw new Exception("AutoDesign attention review failed.");
        }
    }
}
