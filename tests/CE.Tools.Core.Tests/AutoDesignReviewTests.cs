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
            var plan = new AutoDesignEngine().Plan(new[]
            {
                new AutoDesignStep { Id = "approved", Description = "Place profiles", ApprovalReason = "Use CE_SEWPROFILE to choose placement.", Execute = () => executions++ },
                new AutoDesignStep { Id = "blocked", Description = "Validate profiles", Dependencies = new[] { "approved" } },
                new AutoDesignStep { Id = "done", Description = "Successful work" }
            }, AutoDesignMode.CompleteExistingDesign, "drawing", new Dictionary<string, AutoDesignCheckpoint>());
            plan[2].Status = AutoDesignStatus.Completed;
            string text = AutoDesignReview.AttentionText(plan, new[] { "Check cover", "Check cover" });
            Check(text.Contains("Approval decisions: 1; failed: 0; blocked: 1; validation findings: 1"));
            Check(text.Contains("Use CE_SEWPROFILE to choose placement.") && text.Contains("Blocked: Validate profiles"));
            Check(!text.Contains("Successful work") && executions == 0 && plan[0].Status == AutoDesignStatus.NeedsApproval);
            plan[1].Status = AutoDesignStatus.Failed;
            plan[1].Message = "Native refresh failed";
            Check(AutoDesignReview.AttentionText(plan, new string[0]).Contains("Native refresh failed"));
            plan[1].Status = AutoDesignStatus.Cancelled;
            Check(AutoDesignReview.AttentionText(plan, new string[0]).Contains("Cancelled: Validate profiles"));
            Check(AutoDesignReview.AttentionText(new AutoDesignItem[0], new string[0]).Contains("No outstanding automation items"));
            return 6;
        }

        private static void Check(bool condition)
        {
            if (!condition) throw new Exception("AutoDesign attention review failed.");
        }
    }
}
