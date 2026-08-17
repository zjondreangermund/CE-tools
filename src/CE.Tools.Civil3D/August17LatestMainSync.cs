namespace CETools.Civil3D
{
    /// <summary>
    /// Source-visible marker for the 17 August 2026 Project/Survey production field-test sync.
    /// The final Civil 3D 2023 staging repair enforces these same expectations after all older
    /// August compatibility injectors have run.
    /// </summary>
    internal static class August17LatestMainSync
    {
        internal const string SyncId = "2026-08-17-project-survey-frontdoor-3";
        internal const bool ProjectProductionIncludesSurveyLocation = false;
        internal const bool ProjectProductionIncludesNamibiaLo = false;
        internal const bool SurveyProductionOwnsSurveyLocationAndNamibiaLo = true;
        internal const bool DisciplineStylePresetsBeforeProjectStyleCentre = true;
        internal const bool TownAndCrsDriveLoCentralMeridian = true;
        internal const bool DrawingAndClientBooksUseRegisteredTitleBlockSource = true;
        internal const bool ProjectFrontDoorRoutesToStructuredPage = true;
        internal const bool SurveyFrontDoorRoutesToStructuredPage = true;
    }
}
