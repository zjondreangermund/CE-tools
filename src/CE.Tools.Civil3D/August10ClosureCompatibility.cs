namespace CETools.Civil3D
{
    internal static class August10ClosureCompatibility
    {
        internal static void RoadLayoutRefresh(this RoadLayoutProductionCommands commands)
        {
            if (commands != null) commands.Refresh();
        }
    }
}
