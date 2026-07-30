# Preserved V54 Civil 3D source inventory

This inventory was captured from the uploaded V54 source pack before active-source reconciliation. It is a preservation checklist, not permission to overwrite newer repository implementations.

## Source files

- AdvancedParkingPlanningCommands.cs
- AlignmentAnnotationLinkStore.cs
- AlignmentCommands.cs
- AnnotationCommands.cs
- AnnotationScaleSyncCommands.cs
- AutoCADTypeAliases.cs
- BackgroundXrefManagementCommands.cs
- BellmouthDensifier.cs
- BillOfQuantitiesCommands.cs
- CE.Tools.Civil3D.csproj
- CivilObjectBatchStyleCommands.cs
- ClientBookCommands.cs
- ClosedParkingBayWorkflow.cs
- ColourCommands.cs
- CommentPresentationCommands.cs
- CoordinateCommands.cs
- CoordinatePolylineCommands.cs
- CoordinateSystemCommands.cs
- CorridorAnnotationLinkStore.cs
- CorridorCommands.cs
- DesignStandardsLibraryCommands.cs
- DetailedSectionAnnotationCommands.cs
- DrawingCleanupCommands.cs
- DynamicCoordinateLinkStore.cs
- DynamicCrossSectionCommands.cs
- DynamicIntersectionCommands.cs
- DynamicTypicalDetailCommands.cs
- DynamicTypicalDetailEngine.cs
- DynamicTypicalDetailStorage.cs
- EngineeringAssetLibraryCommands.cs
- FastBlockEditCommands.cs
- FeatureLineCommands.cs
- FeatureLineConstructionCommands.cs
- FeatureLineRelativeCommands.cs
- FeatureLineWeedCommands.cs
- FeatureProfileSurfaceCommentCommands.cs
- FloatingToolsWindow.cs
- FloodResultReviewCommands.cs
- FlowNetworkCulvertCommands.cs
- GradingDrainageDiagnosticCommands.cs
- GridReportPresenter.cs
- HatchCommands.cs
- HydraulicReviewCommands.cs
- ModelDesignAuditCommands.cs
- NetworkAssetScheduleCommands.cs
- NetworkCommentCommands.cs
- ParkingCommands.cs
- ParkingDynamicGradingCommands.cs
- ParkingNumberLinkStore.cs
- ParkingOptimiserCommands.cs
- ParkingReportLinkStore.cs
- ParkingSkewValidationCommands.cs
- PluginEntry.cs
- PolylineDirectionCommands.cs
- PopupTablePresenter.cs
- ProductionCommentCommands.cs
- ProductionReportCommands.cs
- ProfileAnnotationLinkStore.cs
- ProfileCommands.cs
- ProfileStationInputWindow.cs
- ProfileStyleLinker.cs
- ProfileViewBatchCommands.cs
- ProjectPresentationCommands.cs
- ProjectSetupCommands.cs
- ProjectSetupPopupWindow.cs
- ProjectStyleCenterCommands.cs
- PumpSystemReviewCommands.cs
- QuantityCommands.cs
- ReportPresentationCommands.cs
- ReturnPeriodHydrographCommands.cs
- RibbonIconCommands.cs
- RibbonVisuals.cs
- RoadCrossSectionScheduleCommands.cs
- RoadDriveReviewCommands.cs
- RoadProductionCommentCommands.cs
- SettingOutScheduleCommands.cs
- SewerBranchAlignmentCommands.cs
- SewerExcavationCommentCommands.cs
- SewerLabelLayoutCommands.cs
- SewerProductionCommands.cs
- SewerSequenceCommands.cs
- SpecialistModelExchangeCommands.cs
- StandardQuantityTemplateCommands.cs
- StandardsSelectionCommands.cs
- StormwaterProductionCommands.cs
- StormwaterSequenceCommands.cs
- SurfaceCommands.cs
- SurfaceComparisonLinkStore.cs
- SurfaceCorrectionCommands.cs
- SurfaceHydrologyCommands.cs
- SurfacePondingCommands.cs
- SurfaceSpikeHoleRepairCommands.cs
- SurveyCoordinateWorkflowCommands.cs
- SurveyCorrectionComparisonCommands.cs
- TypicalDetailsCommands.cs
- TypicalDetailsReviewCommands.cs
- TypicalDetailsRibbonExtension.cs
- WaterProductionCommands.cs
- WaterSewerCostEstimateCommands.cs
- WorkflowRepairCommands.cs
- XrefProjectManagementCommands.cs

## Non-negotiable recovered behaviour

- Sewer branch names are offset from their generated alignments using a scale-aware paper-distance calculation and an approved minimum factor of `2.75`.
- Branch labels repeat along long branches, rotate with the local alignment direction and retain background fill.
- Civil 3D 2023 is the default local build host.
- Windows Forms is referenced for file and confirmation dialogs used by production commands.
- Build output is copied into the versioned Autodesk application-bundle folder.

## Reconciliation rule

For every file above:

1. compare the preserved source with the current repository implementation;
2. retain the newer implementation when it contains all preserved behaviour;
3. merge only missing functionality and compile repairs;
4. add a validator for every regression that previously occurred;
5. do not merge the preservation pull request until Civil 3D 2023 compilation and in-product smoke testing pass.
