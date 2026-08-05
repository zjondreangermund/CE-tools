# Production popup target scan

## AnnotationCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 569-642
```csharp
00569:         [CommandMethod(
00570:             "CE_TOOLS",
00571:             "CE_PKNUMBERX",
00572:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00573:         public void ParkingNumbering()
00574:         {
00575:             Document document = ActiveDocument();
00576:             if (document == null) return;
00577: 
00578:             AnnotationOptions settings;
00579:             if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;
00580: 
00581:             Editor editor = document.Editor;
00582:             PromptSelectionResult selection = GetSelection(
00583:                 editor,
00584:                 "\nSelect parking bay blocks and/or closed bay polylines to number: ");
00585:             if (selection.Status != PromptStatus.OK) return;
00586: 
00587:             PromptResult prefixResult = editor.GetString(
00588:                 new PromptStringOptions("\nEnter bay number prefix <P>: ")
00589:                 {
00590:                     AllowSpaces = false,
00591:                     DefaultValue = "P",
00592:                     UseDefaultValue = true
00593:                 });
00594:             if (prefixResult.Status != PromptStatus.OK) return;
00595: 
00596:             PromptIntegerResult startResult = editor.GetInteger(
00597:                 new PromptIntegerOptions("\nEnter starting number <1>: ")
00598:                 {
00599:                     AllowNone = true,
00600:                     DefaultValue = 1,
00601:                     UseDefaultValue = true
00602:                 });
00603:             if (startResult.Status != PromptStatus.OK) return;
00604: 
00605:             PromptIntegerResult incrementResult = editor.GetInteger(
00606:                 new PromptIntegerOptions("\nEnter numbering increment <1>: ")
00607:                 {
00608:                     AllowNone = true,
00609:                     DefaultValue = 1,
00610:                     UseDefaultValue = true
00611:                 });
00612:             if (incrementResult.Status != PromptStatus.OK) return;
00613:             if (incrementResult.Value == 0)
00614:             {
00615:                 editor.WriteMessage("\nCE_PKNUMBERX cancelled. Increment cannot be zero.");
00616:                 return;
00617:             }
00618: 
00619:             int accepted = 0;
00620:             int skipped = 0;
00621:             using (Transaction previewTransaction =
00622:                 document.Database.TransactionManager.StartTransaction())
00623:             {
00624:                 foreach (SelectedObject selectedObject in selection.Value)
00625:                 {
00626:                     Entity entity = OpenNumberableEntity(previewTransaction, selectedObject);
00627:                     Point3d center;
00628:                     if (entity == null ||
00629:                         IsLayerLocked(previewTransaction, entity.LayerId) ||
00630:                         !TryGetCenter(entity, out center))
00631:                     {
00632:                         skipped++;
00633:                     }
00634:                     else
00635:                     {
00636:                         accepted++;
00637:                     }
00638:                 }
00639:             }
00640: 
00641:             if (accepted == 0)
00642:             {
```

### Lines 903-982
```csharp
00903:             {
00904:                 center = Point3d.Origin;
00905:                 return false;
00906:             }
00907:         }
00908: 
00909:         private static bool IsLayerLocked(Transaction transaction, ObjectId layerId)
00910:         {
00911:             if (layerId.IsNull) return false;
00912:             LayerTableRecord layer = transaction.GetObject(
00913:                 layerId,
00914:                 OpenMode.ForRead,
00915:                 false) as LayerTableRecord;
00916:             return layer != null && layer.IsLocked;
00917:         }
00918: 
00919:         private static bool Confirm(Editor editor, string message)
00920:         {
00921:             var options = new PromptKeywordOptions(
00922:                 "\n" + message + "? [Yes/No] <No>: ")
00923:             {
00924:                 AllowNone = true
00925:             };
00926:             options.Keywords.Add("Yes");
00927:             options.Keywords.Add("No");
00928:             PromptResult result = editor.GetKeywords(options);
00929:             return result.Status == PromptStatus.OK &&
00930:                 string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00931:         }
00932:     }
00933: 
00934:     internal enum AnnotationOutput
00935:     {
00936:         MLeader,
00937:         MText,
00938:         Cogo
00939:     }
00940: 
00941:     internal sealed class AnnotationOptions
00942:     {
00943:         public AnnotationOptions(double textHeight, bool drawMarker, AnnotationOutput output)
00944:         {
00945:             TextHeight = textHeight;
00946:             DrawMarker = drawMarker;
00947:             Output = output;
00948:         }
00949: 
00950:         public double TextHeight { get; }
00951:         public bool DrawMarker { get; }
00952:         public AnnotationOutput Output { get; }
00953:     }
00954: 
00955:     internal static class AnnotationSettingsStore
00956:     {
00957:         private const string RootDictionaryName = "CE_TOOLS";
00958:         private const string RecordName = "ANNOTATION_SETTINGS";
00959:         private const string SchemaVersion = "2";
00960: 
00961:         public static bool Prepare(
00962:             Document document,
00963:             bool allowCogo,
00964:             out AnnotationOptions options)
00965:         {
00966:             options = Read(document.Database);
00967:             if (!allowCogo && options.Output == AnnotationOutput.Cogo)
00968:             {
00969:                 options = new AnnotationOptions(
00970:                     options.TextHeight,
00971:                     options.DrawMarker,
00972:                     AnnotationOutput.MLeader);
00973:             }
00974: 
00975:             Editor editor = document.Editor;
00976:             editor.WriteMessage(
00977:                 "\nCE annotation settings: height={0:N1}; marker={1}; output={2}.",
00978:                 options.TextHeight,
00979:                 options.DrawMarker ? "Yes" : "No",
00980:                 options.Output);
00981: 
00982:             string choice = DisciplineWorkflowDialogs.SelectWorkflow(
```

## BackgroundXrefManagementCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 72-151
```csharp
00072:                 audit.TypeCounts.Count,
00073:                 audit.LockedLayerObjects);
00074:         }
00075: 
00076:         [CommandMethod(
00077:             "CE_TOOLS",
00078:             "CE_BACKGROUNDLIGHT",
00079:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00080:         public void CreateLightBackground()
00081:         {
00082:             Document document = ActiveDocument();
00083:             if (document == null) return;
00084:             Editor editor = document.Editor;
00085:             PromptSelectionResult selection = GetSelection(
00086:                 editor,
00087:                 "\nSelect architectural/survey objects for controlled light-background presentation: ");
00088:             if (selection.Status != PromptStatus.OK) return;
00089: 
00090:             var modeOptions = new PromptKeywordOptions(
00091:                 "\nBackground operation [Copy/Move] <Copy>: ")
00092:             {
00093:                 AllowNone = true
00094:             };
00095:             modeOptions.Keywords.Add("Copy");
00096:             modeOptions.Keywords.Add("Move");
00097:             PromptResult modeResult = editor.GetKeywords(modeOptions);
00098:             if (modeResult.Status == PromptStatus.Cancel) return;
00099:             bool copy = modeResult.Status != PromptStatus.OK ||
00100:                 string.Equals(modeResult.StringResult, "Copy", StringComparison.OrdinalIgnoreCase);
00101: 
00102:             BackgroundAudit audit = ReadAudit(document.Database, selection);
00103:             var review = new List<KeyValuePair<string, string>>
00104:             {
00105:                 Pair("Selected objects", audit.ObjectCount.ToString(CultureInfo.InvariantCulture)),
00106:                 Pair("Source layers", audit.LayerCounts.Count.ToString(CultureInfo.InvariantCulture)),
00107:                 Pair("Operation", copy ? "Create light-background copies" : "Move selected objects to light-background layers"),
00108:                 Pair("Background colour", BackgroundColour.ToString(CultureInfo.InvariantCulture)),
00109:                 Pair("Layer naming", BackgroundPrefix + "<source layer>"),
00110:                 Pair("Result remains selected", "Yes")
00111:             };
00112:             if (!PopupTablePresenter.ShowReview(
00113:                     "CE Tools - Light Background",
00114:                     copy
00115:                         ? "Copies will be placed on controlled CE background layers. Original objects remain unchanged."
00116:                         : "Selected objects will be moved to controlled CE background layers. No geometry is deleted.",
00117:                     review,
00118:                     copy ? "Create Copies" : "Move Objects"))
00119:             {
00120:                 editor.WriteMessage("\nCE_BACKGROUNDLIGHT cancelled.");
00121:                 return;
00122:             }
00123: 
00124:             try
00125:             {
00126:                 ObjectId[] resultIds = ApplyLightBackground(
00127:                     document.Database,
00128:                     selection,
00129:                     copy);
00130:                 editor.SetImpliedSelection(resultIds);
00131:                 editor.Regen();
00132:                 editor.WriteMessage(
00133:                     "\nCE_BACKGROUNDLIGHT complete. Result objects={0}; mode={1}. The result remains selected for Properties inspection.",
00134:                     resultIds.Length,
00135:                     copy ? "Copy" : "Move");
00136:             }
00137:             catch (System.Exception exception)
00138:             {
00139:                 editor.WriteMessage(
00140:                     "\nCE_BACKGROUNDLIGHT cancelled. No background transaction was committed. {0}",
00141:                     exception.Message);
00142:             }
00143:         }
00144: 
00145:         [CommandMethod(
00146:             "CE_TOOLS",
00147:             "CE_XREFSPLIT",
00148:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00149:         public void SplitSelectionToXref()
00150:         {
00151:             Document document = ActiveDocument();
```

### Lines 166-245
```csharp
00166:             };
00167:             PromptFileNameResult fileResult = editor.GetFileNameForSave(saveOptions);
00168:             if (fileResult.Status != PromptStatus.OK) return;
00169:             string path = fileResult.StringResult;
00170:             if (!path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
00171:                 path += ".dwg";
00172: 
00173:             PromptPointOptions pointOptions = new PromptPointOptions(
00174:                 "\nSpecify XREF base point or press Enter for 0,0,0: ")
00175:             {
00176:                 AllowNone = true
00177:             };
00178:             PromptPointResult pointResult = editor.GetPoint(pointOptions);
00179:             if (pointResult.Status == PromptStatus.Cancel) return;
00180:             Point3d basePoint = pointResult.Status == PromptStatus.OK
00181:                 ? pointResult.Value
00182:                 : Point3d.Origin;
00183: 
00184:             var sourceOptions = new PromptKeywordOptions(
00185:                 "\nAfter attaching the XREF [Keep/Replace] original selected objects <Replace>: ")
00186:             {
00187:                 AllowNone = true
00188:             };
00189:             sourceOptions.Keywords.Add("Keep");
00190:             sourceOptions.Keywords.Add("Replace");
00191:             PromptResult sourceResult = editor.GetKeywords(sourceOptions);
00192:             if (sourceResult.Status == PromptStatus.Cancel) return;
00193:             bool replace = sourceResult.Status != PromptStatus.OK ||
00194:                 string.Equals(sourceResult.StringResult, "Replace", StringComparison.OrdinalIgnoreCase);
00195: 
00196:             string xrefName = SanitizeName(Path.GetFileNameWithoutExtension(path));
00197:             var review = new List<KeyValuePair<string, string>>
00198:             {
00199:                 Pair("Objects to export", selection.Value.Count.ToString(CultureInfo.InvariantCulture)),
00200:                 Pair("Output DWG", path),
00201:                 Pair("XREF name", xrefName),
00202:                 Pair("Base point", FormatPoint(basePoint)),
00203:                 Pair("Original objects", replace ? "Replace after successful attach" : "Keep"),
00204:                 Pair("Revision folder", Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "Revisions"))
00205:             };
00206:             if (!PopupTablePresenter.ShowReview(
00207:                     "CE Tools - Split Selection to XREF",
00208:                     "The selected objects and required drawing dependencies will be written to a separate DWG. The new file is then attached to the current drawing.",
00209:                     review,
00210:                     "Create XREF"))
00211:             {
00212:                 editor.WriteMessage("\nCE_XREFSPLIT cancelled.");
00213:                 return;
00214:             }
00215: 
00216:             try
00217:             {
00218:                 CreateXrefFile(document.Database, selection, basePoint, path);
00219:                 ObjectId referenceId = AttachXref(
00220:                     document.Database,
00221:                     selection,
00222:                     basePoint,
00223:                     path,
00224:                     xrefName,
00225:                     replace);
00226:                 editor.SetImpliedSelection(new[] { referenceId });
00227:                 editor.Regen();
00228:                 editor.WriteMessage(
00229:                     "\nCE_XREFSPLIT complete. File={0}; XREF={1}; originals={2}.",
00230:                     path,
00231:                     xrefName,
00232:                     replace ? "replaced" : "kept");
00233:             }
00234:             catch (System.Exception exception)
00235:             {
00236:                 editor.WriteMessage(
00237:                     "\nCE_XREFSPLIT stopped. {0}",
00238:                     exception.Message);
00239:             }
00240:         }
00241: 
00242:         [CommandMethod("CE_TOOLS", "CE_XREFINFO", CommandFlags.Modal | CommandFlags.Redraw)]
00243:         public void XrefInformation()
00244:         {
00245:             Document document = ActiveDocument();
```

## BellmouthDensifier.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 27-107
```csharp
00027:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00028:         public void Execute()
00029:         {
00030:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00031:             if (document == null)
00032:             {
00033:                 return;
00034:             }
00035: 
00036:             Editor editor = document.Editor;
00037:             Database database = document.Database;
00038: 
00039:             PromptSelectionResult selection = PromptForPolylines(editor);
00040:             if (selection.Status != PromptStatus.OK)
00041:             {
00042:                 return;
00043:             }
00044: 
00045:             PromptKeywordOptions methodOptions = new PromptKeywordOptions(
00046:                 "\nDensification method [Maximum/Number] <Maximum>: ")
00047:             {
00048:                 AllowNone = true
00049:             };
00050:             methodOptions.Keywords.Add(MaximumKeyword);
00051:             methodOptions.Keywords.Add(NumberKeyword);
00052: 
00053:             PromptResult methodResult = editor.GetKeywords(methodOptions);
00054:             if (methodResult.Status == PromptStatus.Cancel)
00055:             {
00056:                 return;
00057:             }
00058: 
00059:             string method = methodResult.Status == PromptStatus.None
00060:                 ? MaximumKeyword
00061:                 : methodResult.StringResult;
00062: 
00063:             double maximumSpacing = 0.0;
00064:             int segmentCount = 0;
00065: 
00066:             if (string.Equals(method, NumberKeyword, StringComparison.OrdinalIgnoreCase))
00067:             {
00068:                 PromptIntegerOptions countOptions = new PromptIntegerOptions(
00069:                     $"\nNumber of equal chainage intervals <{_lastSegmentCount}>: ")
00070:                 {
00071:                     AllowNegative = false,
00072:                     AllowZero = false,
00073:                     DefaultValue = _lastSegmentCount,
00074:                     LowerLimit = 2,
00075:                     UpperLimit = DensifyPlanner.MaximumSupportedSegments,
00076:                     UseDefaultValue = true
00077:                 };
00078: 
00079:                 PromptIntegerResult countResult = editor.GetInteger(countOptions);
00080:                 if (countResult.Status != PromptStatus.OK)
00081:                 {
00082:                     return;
00083:                 }
00084: 
00085:                 segmentCount = countResult.Value;
00086:                 _lastSegmentCount = segmentCount;
00087:             }
00088:             else
00089:             {
00090:                 PromptDoubleOptions spacingOptions = new PromptDoubleOptions(
00091:                     $"\nMaximum equal segment length in drawing units <{_lastMaximumSpacing:0.###}>: ")
00092:                 {
00093:                     AllowNegative = false,
00094:                     AllowZero = false,
00095:                     DefaultValue = _lastMaximumSpacing,
00096:                     UseDefaultValue = true
00097:                 };
00098: 
00099:                 PromptDoubleResult spacingResult = editor.GetDouble(spacingOptions);
00100:                 if (spacingResult.Status != PromptStatus.OK)
00101:                 {
00102:                     return;
00103:                 }
00104: 
00105:                 maximumSpacing = spacingResult.Value;
00106:                 _lastMaximumSpacing = maximumSpacing;
00107:             }
```

## BillOfQuantitiesCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 217-296
```csharp
00217:                     exception.Message);
00218:             }
00219:         }
00220: 
00221:         [CommandMethod(
00222:             "CE_TOOLS",
00223:             "CE_BOQEXPORT",
00224:             CommandFlags.Modal | CommandFlags.Redraw)]
00225:         public void ExportLinked()
00226:         {
00227:             Document document = ActiveDocument();
00228:             if (document == null) return;
00229: 
00230:             PromptEntityResult result = PromptForLinkedTable(
00231:                 document.Editor,
00232:                 "\nSelect linked CE Tools BOQ table to export to Excel: ");
00233:             if (result.Status != PromptStatus.OK) return;
00234: 
00235:             var refreshOptions = new PromptKeywordOptions(
00236:                 "\nRefresh linked quantities before export? [Yes/No] <Yes>: ")
00237:             {
00238:                 AllowNone = true
00239:             };
00240:             refreshOptions.Keywords.Add("Yes");
00241:             refreshOptions.Keywords.Add("No");
00242:             PromptResult refreshResult = document.Editor.GetKeywords(refreshOptions);
00243:             if (refreshResult.Status == PromptStatus.Cancel) return;
00244: 
00245:             bool refresh = refreshResult.Status == PromptStatus.None ||
00246:                 Equal(refreshResult.StringResult, "Yes");
00247:             if (refresh && !RefreshTable(document, result.ObjectId, false)) return;
00248: 
00249:             string path;
00250:             if (!PromptExcelPath(document.Editor, "CE-Tools-BOQ.xlsx", out path)) return;
00251: 
00252:             try
00253:             {
00254:                 List<IList<string>> cells;
00255:                 string title;
00256:                 using (Transaction transaction =
00257:                     document.Database.TransactionManager.StartTransaction())
00258:                 {
00259:                     Table table = transaction.GetObject(
00260:                         result.ObjectId,
00261:                         OpenMode.ForRead,
00262:                         false) as Table;
00263:                     BoqLink link = ReadLink(table, transaction);
00264:                     title = link.Discipline + " BOQ";
00265:                     cells = ReadTableCells(table);
00266:                 }
00267: 
00268:                 SimpleXlsxWriter.Write(path, title, cells);
00269:                 document.Editor.WriteMessage(
00270:                     "\nCE_BOQEXPORT complete. Excel workbook: {0}",
00271:                     path);
00272:             }
00273:             catch (System.Exception exception)
00274:             {
00275:                 document.Editor.WriteMessage(
00276:                     "\nCE_BOQEXPORT failed. {0}",
00277:                     exception.Message);
00278:             }
00279:         }
00280: 
00281:         [CommandMethod("CE_TOOLS", "CE_BOQROAD", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00282:         public void ExportRoad() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Road); }
00283: 
00284:         [CommandMethod("CE_TOOLS", "CE_BOQPLATFORM", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00285:         public void ExportPlatform() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Platform); }
00286: 
00287:         [CommandMethod("CE_TOOLS", "CE_BOQSTORM", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00288:         public void ExportStormwater() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Stormwater); }
00289: 
00290:         [CommandMethod("CE_TOOLS", "CE_BOQSEWER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00291:         public void ExportSewer() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Sewer); }
00292: 
00293:         [CommandMethod("CE_TOOLS", "CE_BOQWATER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00294:         public void ExportWater() { ExportDiscipline(ActiveDocument(), BoqDiscipline.Water); }
00295: 
00296:         [CommandMethod("CE_TOOLS", "CE_BOQBULKWATER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
```

### Lines 1484-1632
```csharp
01484:                     MessageForAdding = message,
01485:                     AllowDuplicates = false,
01486:                     RejectObjectsFromNonCurrentSpace = true
01487:                 });
01488:         }
01489: 
01490:         private static PromptEntityResult PromptForLinkedTable(
01491:             Editor editor,
01492:             string message)
01493:         {
01494:             var options = new PromptEntityOptions(message);
01495:             options.SetRejectMessage("\nSelect an AutoCAD table.");
01496:             options.AddAllowedClass(typeof(Table), false);
01497:             return editor.GetEntity(options);
01498:         }
01499: 
01500:         private static bool PromptDiscipline(Editor editor, out BoqDiscipline discipline)
01501:         {
01502:             var options = new PromptKeywordOptions(
01503:                 "\nBOQ discipline [General/Road/Platform/Stormwater/Sewer/Water/BulkWater] <General>: ")
01504:             {
01505:                 AllowNone = true
01506:             };
01507:             foreach (string keyword in new[]
01508:             {
01509:                 "General", "Road", "Platform", "Stormwater", "Sewer", "Water", "BulkWater"
01510:             })
01511:                 options.Keywords.Add(keyword);
01512: 
01513:             PromptResult result = editor.GetKeywords(options);
01514:             if (result.Status == PromptStatus.Cancel)
01515:             {
01516:                 discipline = BoqDiscipline.General;
01517:                 return false;
01518:             }
01519: 
01520:             discipline = result.Status == PromptStatus.None
01521:                 ? BoqDiscipline.General
01522:                 : ParseDiscipline(result.StringResult);
01523:             return true;
01524:         }
01525: 
01526:         private static bool PromptUnitsPerMetre(Editor editor, out double unitsPerMetre)
01527:         {
01528:             var options = new PromptDoubleOptions(
01529:                 "\nDrawing units per metre <1.0>: ")
01530:             {
01531:                 AllowNone = true,
01532:                 AllowNegative = false,
01533:                 AllowZero = false,
01534:                 DefaultValue = 1.0,
01535:                 UseDefaultValue = true
01536:             };
01537:             PromptDoubleResult result = editor.GetDouble(options);
01538:             unitsPerMetre = result.Status == PromptStatus.OK
01539:                 ? result.Value
01540:                 : 1.0;
01541:             return result.Status == PromptStatus.OK && IsFinitePositive(unitsPerMetre);
01542:         }
01543: 
01544:         private static bool PromptExcelPath(
01545:             Editor editor,
01546:             string defaultName,
01547:             out string path)
01548:         {
01549:             var options = new PromptSaveFileOptions(
01550:                 "\nSelect Excel workbook output path: ")
01551:             {
01552:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
01553:                 DialogCaption = "Export CE Tools Bill of Quantities",
01554:                 InitialFileName = defaultName
01555:             };
01556:             PromptFileNameResult result = editor.GetFileNameForSave(options);
01557:             if (result.Status != PromptStatus.OK)
01558:             {
01559:                 path = string.Empty;
01560:                 return false;
01561:             }
01562: 
01563:             path = result.StringResult;
01564:             if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
01565:                 path += ".xlsx";
01566:             return true;
01567:         }
01568: 
01569:         private static bool Confirm(Editor editor, string message)
01570:         {
01571:             var options = new PromptKeywordOptions(
01572:                 "\n" + message + "? [Yes/No] <No>: ")
01573:             {
01574:                 AllowNone = true
01575:             };
01576:             options.Keywords.Add("Yes");
01577:             options.Keywords.Add("No");
01578:             PromptResult result = editor.GetKeywords(options);
01579:             return result.Status == PromptStatus.OK &&
01580:                 Equal(result.StringResult, "Yes");
01581:         }
01582: 
01583:         private static double ResolveTableTextHeight(Database database)
01584:         {
01585:             double height = database == null ? 2.0 : database.Textsize;
01586:             if (Math.Abs(height - 1.8) < 0.05) return 1.8;
01587:             if (Math.Abs(height - 5.0) < 0.05) return 5.0;
01588:             return 2.0;
01589:         }
01590: 
01591:         private static string GetCell(Table table, int row, int column)
01592:         {
01593:             try
01594:             {
01595:                 return table.Cells[row, column].TextString ?? string.Empty;
01596:             }
01597:             catch
01598:             {
01599:                 return string.Empty;
01600:             }
01601:         }
01602: 
01603:         private static bool TryParseNumber(string text, out double value)
01604:         {
01605:             return double.TryParse(
01606:                        text,
01607:                        NumberStyles.Float | NumberStyles.AllowThousands,
01608:                        CultureInfo.CurrentCulture,
01609:                        out value) ||
01610:                    double.TryParse(
01611:                        text,
01612:                        NumberStyles.Float | NumberStyles.AllowThousands,
01613:                        CultureInfo.InvariantCulture,
01614:                        out value);
01615:         }
01616: 
01617:         private static bool ContainsAny(string source, params string[] values)
01618:         {
01619:             if (string.IsNullOrEmpty(source)) return false;
01620:             foreach (string value in values)
01621:             {
01622:                 if (source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
01623:                     return true;
01624:             }
01625:             return false;
01626:         }
01627: 
01628:         private static string FriendlyTypeName(string typeName)
01629:         {
01630:             if (string.IsNullOrWhiteSpace(typeName)) return "Design item";
01631:             var builder = new StringBuilder(typeName.Length + 8);
01632:             for (int index = 0; index < typeName.Length; index++)
```

## ClientBookCommands.cs
Hits: `CE_CLIENTBOOK`, `CE_PROJECTCLOSEOUT`, `BuildTitleBlock`, `DrawingRegister`, `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 13-370
```csharp
00013: [assembly: CommandClass(typeof(CETools.Civil3D.ClientBookCommands))]
00014: 
00015: namespace CETools.Civil3D
00016: {
00017:     /// <summary>
00018:     /// Creates a presentation-ready, linked A4/A3 client book at project closeout.
00019:     /// Pages are regenerated from current project metadata, model-space inventory,
00020:     /// linked BOQs, linked dynamic sections and existing project layouts.
00021:     /// </summary>
00022:     public sealed class ClientBookCommands
00023:     {
00024:         private const string LinkRecordName = "CE_CLIENT_BOOK_PAGE";
00025:         private const string SchemaVersion = "1";
00026:         private const string ProjectRootDictionary = "CE_TOOLS";
00027:         private const string ProjectMetadataRecord = "PROJECT_METADATA";
00028: 
00029:         [CommandMethod(
00030:             "CE_TOOLS",
00031:             "CE_PROJECTCLOSEOUT",
00032:             CommandFlags.Modal | CommandFlags.Redraw)]
00033:         public void ProjectCloseout()
00034:         {
00035:             Document document = ActiveDocument();
00036:             if (document == null) return;
00037:             CreateClientBook(document, ClientPaperSelection.Both, true);
00038:         }
00039: 
00040:         [CommandMethod(
00041:             "CE_TOOLS",
00042:             "CE_CLIENTBOOK",
00043:             CommandFlags.Modal | CommandFlags.Redraw)]
00044:         public void ClientBook()
00045:         {
00046:             Document document = ActiveDocument();
00047:             if (document == null) return;
00048: 
00049:             ClientPaperSelection selection;
00050:             if (!PromptPaperSelection(document.Editor, out selection)) return;
00051:             CreateClientBook(document, selection, false);
00052:         }
00053: 
00054:         [CommandMethod(
00055:             "CE_TOOLS",
00056:             "CE_CLIENTBOOKREFRESH",
00057:             CommandFlags.Modal | CommandFlags.Redraw)]
00058:         public void RefreshClientBook()
00059:         {
00060:             Document document = ActiveDocument();
00061:             if (document == null) return;
00062: 
00063:             List<ClientPageLink> existing = ReadAllClientPageLinks(document.Database);
00064:             if (existing.Count == 0)
00065:             {
00066:                 document.Editor.WriteMessage(
00067:                     "\nCE_CLIENTBOOKREFRESH: no linked CE Tools client-book pages were found.");
00068:                 return;
00069:             }
00070: 
00071:             ClientSnapshot snapshot = BuildSnapshot(document.Database);
00072:             WriteSnapshotPreview(document.Editor, snapshot);
00073:             if (!Confirm(
00074:                 document.Editor,
00075:                 "Refresh all linked A4/A3 client-book pages from the current project"))
00076:                 return;
00077: 
00078:             int refreshed = 0;
00079:             int failed = 0;
00080:             foreach (ClientPageLink link in existing)
00081:             {
00082:                 ClientPageDefinition page = FindPageDefinition(
00083:                     link.Paper,
00084:                     link.PageKey);
00085:                 if (page == null)
00086:                 {
00087:                     failed++;
00088:                     continue;
00089:                 }
00090: 
00091:                 try
00092:                 {
00093:                     CreateOrRefreshPage(
00094:                         document.Database,
00095:                         page,
00096:                         link.Stage,
00097:                         link.Revision,
00098:                         snapshot);
00099:                     refreshed++;
00100:                 }
00101:                 catch (System.Exception exception)
00102:                 {
00103:                     failed++;
00104:                     document.Editor.WriteMessage(
00105:                         "\n  Failed to refresh {0}: {1}",
00106:                         link.LayoutName,
00107:                         exception.Message);
00108:                 }
00109:             }
00110: 
00111:             document.Editor.WriteMessage(
00112:                 "\nCE_CLIENTBOOKREFRESH complete. Refreshed={0}; failed={1}.",
00113:                 refreshed,
00114:                 failed);
00115:         }
00116: 
00117:         [CommandMethod(
00118:             "CE_TOOLS",
00119:             "CE_CLIENTBOOKINFO",
00120:             CommandFlags.Modal | CommandFlags.Redraw)]
00121:         public void ClientBookInformation()
00122:         {
00123:             Document document = ActiveDocument();
00124:             if (document == null) return;
00125: 
00126:             List<ClientPageLink> links = ReadAllClientPageLinks(document.Database);
00127:             var rows = new List<IList<string>>();
00128:             foreach (ClientPageLink link in links
00129:                 .OrderBy(item => item.Paper)
00130:                 .ThenBy(item => item.PageNumber))
00131:             {
00132:                 int valid = 0;
00133:                 int stale = 0;
00134:                 foreach (string handle in link.GeneratedHandles)
00135:                 {
00136:                     ObjectId id;
00137:                     if (TryResolveHandle(document.Database, handle, out id)) valid++;
00138:                     else stale++;
00139:                 }
00140: 
00141:                 rows.Add(new List<string>
00142:                 {
00143:                     link.Paper,
00144:                     link.PageNumber,
00145:                     link.Title,
00146:                     link.LayoutName,
00147:                     link.Stage,
00148:                     link.Revision,
00149:                     valid.ToString(CultureInfo.InvariantCulture),
00150:                     stale.ToString(CultureInfo.InvariantCulture)
00151:                 });
00152:             }
00153: 
00154:             if (rows.Count == 0)
00155:             {
00156:                 rows.Add(new List<string>
00157:                 {
00158:                     string.Empty,
00159:                     string.Empty,
00160:                     "No linked client-book pages",
00161:                     string.Empty,
00162:                     string.Empty,
00163:                     string.Empty,
00164:                     "0",
00165:                     "0"
00166:                 });
00167:             }
00168: 
00169:             GridReportPresenter.ShowReportAndOfferTable(
00170:                 document,
00171:                 "CE Tools Client Book",
00172:                 "A4/A3 client-book pages are linked to current drawing information through CE_CLIENTBOOKREFRESH.",
00173:                 new List<string>
00174:                 {
00175:                     "Paper", "Sheet", "Title", "Layout", "Stage", "Revision",
00176:                     "Valid Objects", "Stale Handles"
00177:                 },
00178:                 rows,
00179:                 "CE TOOLS CLIENT BOOK REGISTER");
00180:         }
00181: 
00182:         [CommandMethod(
00183:             "CE_TOOLS",
00184:             "CE_CLIENTBOOKINDEX",
00185:             CommandFlags.Modal | CommandFlags.Redraw)]
00186:         public void ExportClientBookIndex()
00187:         {
00188:             Document document = ActiveDocument();
00189:             if (document == null) return;
00190: 
00191:             List<ClientPageLink> links = ReadAllClientPageLinks(document.Database);
00192:             if (links.Count == 0)
00193:             {
00194:                 document.Editor.WriteMessage(
00195:                     "\nCE_CLIENTBOOKINDEX: create a client book before exporting its index.");
00196:                 return;
00197:             }
00198: 
00199:             string path;
00200:             if (!PromptExcelPath(
00201:                 document.Editor,
00202:                 "CE-Tools-Client-Book-Index.xlsx",
00203:                 out path)) return;
00204: 
00205:             var rows = new List<IList<string>>
00206:             {
00207:                 new List<string>
00208:                 {
00209:                     "CE TOOLS CLIENT BOOK INDEX", string.Empty, string.Empty,
00210:                     string.Empty, string.Empty, string.Empty
00211:                 },
00212:                 new List<string>
00213:                 {
00214:                     "PAPER", "SHEET", "TITLE", "LAYOUT", "STAGE", "REVISION"
00215:                 }
00216:             };
00217:             foreach (ClientPageLink link in links
00218:                 .OrderBy(item => item.Paper)
00219:                 .ThenBy(item => item.PageNumber))
00220:             {
00221:                 rows.Add(new List<string>
00222:                 {
00223:                     link.Paper,
00224:                     link.PageNumber,
00225:                     link.Title,
00226:                     link.LayoutName,
00227:                     link.Stage,
00228:                     link.Revision
00229:                 });
00230:             }
00231: 
00232:             try
00233:             {
00234:                 SimpleXlsxWriter.Write(path, "Client Book Index", rows);
00235:                 document.Editor.WriteMessage(
00236:                     "\nCE_CLIENTBOOKINDEX complete. Pages={0}; workbook={1}",
00237:                     links.Count,
00238:                     path);
00239:             }
00240:             catch (System.Exception exception)
00241:             {
00242:                 document.Editor.WriteMessage(
00243:                     "\nCE_CLIENTBOOKINDEX failed. {0}",
00244:                     exception.Message);
00245:             }
00246:         }
00247: 
00248:         private static void CreateClientBook(
00249:             Document document,
00250:             ClientPaperSelection initialSelection,
00251:             bool closeoutMode)
00252:         {
00253:             ClientPaperSelection selection = initialSelection;
00254:             if (closeoutMode)
00255:             {
00256:                 document.Editor.WriteMessage(
00257:                     "\nCE Project Closeout creates or refreshes both the A4 and A3 client summary books.");
00258:             }
00259: 
00260:             string stage;
00261:             if (!PromptStage(document.Editor, out stage)) return;
00262:             string revision;
00263:             if (!PromptRevision(document.Editor, out revision)) return;
00264: 
00265:             ClientSnapshot snapshot = BuildSnapshot(document.Database);
00266:             WriteSnapshotPreview(document.Editor, snapshot);
00267:             List<ClientPageDefinition> pages = PageDefinitions(selection);
00268:             document.Editor.WriteMessage(
00269:                 "\nClient-book preview. Paper={0}; pages={1}; stage={2}; revision={3}.",
00270:                 selection,
00271:                 pages.Count,
00272:                 stage,
00273:                 revision);
00274:             foreach (IGrouping<string, ClientPageDefinition> group in pages.GroupBy(item => item.Paper))
00275:             {
00276:                 document.Editor.WriteMessage(
00277:                     "\n  {0}: {1} linked summary sheets.",
00278:                     group.Key,
00279:                     group.Count());
00280:             }
00281: 
00282:             if (!Confirm(
00283:                 document.Editor,
00284:                 "Create or refresh the linked client summary book"))
00285:                 return;
00286: 
00287:             int created = 0;
00288:             int refreshed = 0;
00289:             int failed = 0;
00290:             foreach (ClientPageDefinition page in pages)
00291:             {
00292:                 try
00293:                 {
00294:                     bool wasCreated = CreateOrRefreshPage(
00295:                         document.Database,
00296:                         page,
00297:                         stage,
00298:                         revision,
00299:                         snapshot);
00300:                     if (wasCreated) created++;
00301:                     else refreshed++;
00302:                 }
00303:                 catch (System.Exception exception)
00304:                 {
00305:                     failed++;
00306:                     document.Editor.WriteMessage(
00307:                         "\n  Failed to generate {0}: {1}",
00308:                         page.LayoutName,
00309:                         exception.Message);
00310:                 }
00311:             }
00312: 
00313:             document.Editor.WriteMessage(
00314:                 "\n{0} complete. Pages created={1}; refreshed={2}; failed={3}. " +
00315:                 "Run CE_CLIENTBOOKREFRESH after project, quantity, section or layout changes.",
00316:                 closeoutMode ? "CE_PROJECTCLOSEOUT" : "CE_CLIENTBOOK",
00317:                 created,
00318:                 refreshed,
00319:                 failed);
00320:         }
00321: 
00322:         private static bool CreateOrRefreshPage(
00323:             Database database,
00324:             ClientPageDefinition page,
00325:             string stage,
00326:             string revision,
00327:             ClientSnapshot snapshot)
00328:         {
00329:             bool created = false;
00330:             ObjectId layoutId = FindLayoutId(database, page.LayoutName);
00331:             if (layoutId.IsNull)
00332:             {
00333:                 layoutId = LayoutManager.Current.CreateLayout(page.LayoutName);
00334:                 created = true;
00335:             }
00336: 
00337:             using (Transaction transaction = database.TransactionManager.StartTransaction())
00338:             {
00339:                 Layout layout = transaction.GetObject(
00340:                     layoutId,
00341:                     OpenMode.ForWrite,
00342:                     false) as Layout;
00343:                 if (layout == null)
00344:                     throw new InvalidOperationException(
00345:                         "Layout could not be opened: " + page.LayoutName);
00346: 
00347:                 ClientPageLink oldLink = ReadClientPageLinkIfPresent(layout, transaction);
00348:                 if (oldLink != null)
00349:                     EraseGenerated(database, transaction, oldLink.GeneratedHandles);
00350: 
00351:                 BlockTableRecord paperSpace = transaction.GetObject(
00352:                     layout.BlockTableRecordId,
00353:                     OpenMode.ForWrite,
00354:                     false) as BlockTableRecord;
00355:                 if (paperSpace == null)
00356:                     throw new InvalidOperationException(
00357:                         "Paper space could not be opened: " + page.LayoutName);
00358: 
00359:                 var generated = new List<string>();
00360:                 double margin = page.Paper == "A4" ? 8.0 : 10.0;
00361:                 double titleBlockHeight = page.Paper == "A4" ? 27.0 : 32.0;
00362:                 double bodyText = page.Paper == "A4" ? 2.2 : 2.8;
00363:                 double headingText = page.Paper == "A4" ? 4.2 : 5.5;
00364: 
00365:                 Polyline frame = Rectangle(
00366:                     database,
00367:                     margin,
00368:                     margin,
00369:                     page.Width - margin,
00370:                     page.Height - margin,
```

### Lines 398-543
```csharp
00398: 
00399:                 double contentTop = page.Height - margin - 21.0;
00400:                 double contentBottom = margin + titleBlockHeight + 4.0;
00401:                 CreatePageContent(
00402:                     database,
00403:                     transaction,
00404:                     paperSpace,
00405:                     generated,
00406:                     page,
00407:                     stage,
00408:                     revision,
00409:                     snapshot,
00410:                     margin + 3.0,
00411:                     contentTop,
00412:                     page.Width - margin * 2.0 - 6.0,
00413:                     contentTop - contentBottom,
00414:                     bodyText);
00415: 
00416:                 Table titleBlock = BuildTitleBlock(
00417:                     database,
00418:                     new Point3d(margin, margin + titleBlockHeight, 0.0),
00419:                     page,
00420:                     stage,
00421:                     revision,
00422:                     snapshot,
00423:                     bodyText);
00424:                 AddGenerated(transaction, paperSpace, titleBlock, generated);
00425:                 titleBlock.GenerateLayout();
00426: 
00427:                 WriteClientPageLink(
00428:                     layout,
00429:                     transaction,
00430:                     new ClientPageLink(
00431:                         SchemaVersion,
00432:                         page.LayoutName,
00433:                         page.Paper,
00434:                         page.PageKey,
00435:                         page.PageNumber,
00436:                         page.Title,
00437:                         stage,
00438:                         revision,
00439:                         page.Width,
00440:                         page.Height,
00441:                         generated));
00442:                 transaction.Commit();
00443:             }
00444: 
00445:             return created;
00446:         }
00447: 
00448:         private static void CreatePageContent(
00449:             Database database,
00450:             Transaction transaction,
00451:             BlockTableRecord paperSpace,
00452:             ICollection<string> generated,
00453:             ClientPageDefinition page,
00454:             string stage,
00455:             string revision,
00456:             ClientSnapshot snapshot,
00457:             double x,
00458:             double top,
00459:             double width,
00460:             double availableHeight,
00461:             double textHeight)
00462:         {
00463:             if (page.Kind == ClientPageKind.Cover)
00464:             {
00465:                 CreateCoverContent(
00466:                     database,
00467:                     transaction,
00468:                     paperSpace,
00469:                     generated,
00470:                     page,
00471:                     stage,
00472:                     revision,
00473:                     snapshot,
00474:                     x,
00475:                     top,
00476:                     width,
00477:                     textHeight);
00478:                 return;
00479:             }
00480: 
00481:             Table table;
00482:             if (page.Kind == ClientPageKind.ProjectSummary)
00483:                 table = BuildProjectSummaryTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width);
00484:             else if (page.Kind == ClientPageKind.DesignSummary)
00485:                 table = BuildDesignSummaryTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width);
00486:             else if (page.Kind == ClientPageKind.QuantitySummary)
00487:                 table = BuildQuantitySummaryTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width, page.Paper);
00488:             else if (page.Kind == ClientPageKind.DrawingRegister)
00489:                 table = BuildDrawingRegisterTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width, page.Paper);
00490:             else if (page.Kind == ClientPageKind.SectionRegister)
00491:                 table = BuildSectionRegisterTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width, page.Paper);
00492:             else
00493:                 table = BuildTypicalDetailsTable(database, new Point3d(x, top, 0.0), snapshot, textHeight, width, page.Paper);
00494: 
00495:             AddGenerated(transaction, paperSpace, table, generated);
00496:             table.GenerateLayout();
00497: 
00498:             MText note = Text(
00499:                 database,
00500:                 new Point3d(x, Math.Max(8.0, top - availableHeight + textHeight * 1.5), 0.0),
00501:                 textHeight * 0.75,
00502:                 PageNote(page.Kind),
00503:                 width,
00504:                 8);
00505:             AddGenerated(transaction, paperSpace, note, generated);
00506:         }
00507: 
00508:         private static void CreateCoverContent(
00509:             Database database,
00510:             Transaction transaction,
00511:             BlockTableRecord paperSpace,
00512:             ICollection<string> generated,
00513:             ClientPageDefinition page,
00514:             string stage,
00515:             string revision,
00516:             ClientSnapshot snapshot,
00517:             double x,
00518:             double top,
00519:             double width,
00520:             double textHeight)
00521:         {
00522:             string project = ValueOrNotSet(snapshot.Project.Get("Project Name"));
00523:             string client = ValueOrNotSet(snapshot.Project.Get("Client"));
00524:             string location = JoinLocation(
00525:                 snapshot.Project.Get("Town"),
00526:                 snapshot.Project.Get("Country"));
00527: 
00528:             MText title = Text(
00529:                 database,
00530:                 new Point3d(x + width * 0.08, top - 18.0, 0.0),
00531:                 page.Paper == "A4" ? 8.0 : 12.0,
00532:                 project.ToUpperInvariant() +
00533:                     "\\P\\PCLIENT DESIGN SUMMARY BOOK",
00534:                 width * 0.84,
00535:                 4);
00536:             AddGenerated(transaction, paperSpace, title, generated);
00537: 
00538:             MText details = Text(
00539:                 database,
00540:                 new Point3d(x + width * 0.08, top - (page.Paper == "A4" ? 62.0 : 82.0), 0.0),
00541:                 textHeight * 1.15,
00542:                 "CLIENT: " + client +
00543:                     "\\PLOCATION: " + location +
```

### Lines 671-743
```csharp
00671:             }
00672:             if (rows.Count == 0)
00673:                 rows.Add(new List<string> { "General", "", "No reportable model objects", "0", "", "", "" });
00674:             return BuildTable(
00675:                 database,
00676:                 position,
00677:                 "CURRENT QUANTITY SUMMARY",
00678:                 new[] { "DISCIPLINE", "LAYER", "TYPE", "COUNT", "LENGTH", "AREA", "VOLUME" },
00679:                 rows,
00680:                 new[]
00681:                 {
00682:                     width * 0.14, width * 0.20, width * 0.20, width * 0.08,
00683:                     width * 0.13, width * 0.12, width * 0.13
00684:                 },
00685:                 textHeight * 0.82,
00686:                 1.85);
00687:         }
00688: 
00689:         private static Table BuildDrawingRegisterTable(
00690:             Database database,
00691:             Point3d position,
00692:             ClientSnapshot snapshot,
00693:             double textHeight,
00694:             double width,
00695:             string paper)
00696:         {
00697:             int limit = paper == "A4" ? 14 : 26;
00698:             List<ClientLayoutSnapshot> layouts = snapshot.Layouts
00699:                 .Where(item => !item.Name.StartsWith("CE-CLIENT-", StringComparison.OrdinalIgnoreCase))
00700:                 .OrderBy(item => item.TabOrder)
00701:                 .ThenBy(item => item.Name)
00702:                 .Take(limit)
00703:                 .ToList();
00704:             var rows = new List<IList<string>>();
00705:             for (int index = 0; index < layouts.Count; index++)
00706:             {
00707:                 rows.Add(new List<string>
00708:                 {
00709:                     (index + 1).ToString("D2", CultureInfo.InvariantCulture),
00710:                     layouts[index].Name,
00711:                     "Project drawing / layout",
00712:                     "Available"
00713:                 });
00714:             }
00715:             if (rows.Count == 0)
00716:                 rows.Add(new List<string> { "01", "No project layouts detected", "", "Missing" });
00717:             return BuildTable(
00718:                 database,
00719:                 position,
00720:                 "PROJECT DRAWING REGISTER",
00721:                 new[] { "NO.", "LAYOUT / DRAWING", "PURPOSE", "STATUS" },
00722:                 rows,
00723:                 new[] { width * 0.10, width * 0.45, width * 0.30, width * 0.15 },
00724:                 textHeight,
00725:                 1.9);
00726:         }
00727: 
00728:         private static Table BuildSectionRegisterTable(
00729:             Database database,
00730:             Point3d position,
00731:             ClientSnapshot snapshot,
00732:             double textHeight,
00733:             double width,
00734:             string paper)
00735:         {
00736:             int limit = paper == "A4" ? 14 : 26;
00737:             var rows = new List<IList<string>>();
00738:             int index = 1;
00739:             foreach (ClientSectionSnapshot section in snapshot.Sections.Take(limit))
00740:             {
00741:                 rows.Add(new List<string>
00742:                 {
00743:                     index++.ToString("D2", CultureInfo.InvariantCulture),
```

### Lines 778-850
```csharp
00778:                 {
00779:                     (index + 1).ToString("D2", CultureInfo.InvariantCulture),
00780:                     detail.Title,
00781:                     detail.Discipline,
00782:                     DetailStatus(snapshot, detail)
00783:                 });
00784:             }
00785:             return BuildTable(
00786:                 database,
00787:                 position,
00788:                 "TYPICAL DETAIL SCHEDULE",
00789:                 new[] { "NO.", "DETAIL", "DISCIPLINE", "BOOK STATUS" },
00790:                 rows,
00791:                 new[] { width * 0.08, width * 0.47, width * 0.25, width * 0.20 },
00792:                 textHeight * 0.88,
00793:                 1.8);
00794:         }
00795: 
00796:         private static Table BuildTitleBlock(
00797:             Database database,
00798:             Point3d position,
00799:             ClientPageDefinition page,
00800:             string stage,
00801:             string revision,
00802:             ClientSnapshot snapshot,
00803:             double textHeight)
00804:         {
00805:             var table = new Table();
00806:             table.SetDatabaseDefaults(database);
00807:             table.TableStyle = database.Tablestyle;
00808:             table.Position = position;
00809:             table.SetSize(4, 4);
00810:             table.SetRowHeight(page.Paper == "A4" ? 6.2 : 7.3);
00811:             double total = page.Width - (page.Paper == "A4" ? 16.0 : 20.0);
00812:             table.Columns[0].Width = total * 0.18;
00813:             table.Columns[1].Width = total * 0.47;
00814:             table.Columns[2].Width = total * 0.15;
00815:             table.Columns[3].Width = total * 0.20;
00816:             table.MergeCells(CellRange.Create(table, 0, 0, 0, 3));
00817:             table.Cells[0, 0].TextString =
00818:                 ValueOrNotSet(snapshot.Project.Get("Project Name")) +
00819:                 "  |  " + page.Title;
00820:             table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
00821:             string[,] values =
00822:             {
00823:                 { "CLIENT", ValueOrNotSet(snapshot.Project.Get("Client")), "SHEET", page.PageNumber },
00824:                 { "LOCATION", JoinLocation(snapshot.Project.Get("Town"), snapshot.Project.Get("Country")), "STAGE", stage },
00825:                 { "ISSUE DATE", DateTime.Now.ToString("dd MMM yyyy", CultureInfo.CurrentCulture), "REVISION", revision }
00826:             };
00827:             for (int row = 0; row < 3; row++)
00828:             {
00829:                 for (int column = 0; column < 4; column++)
00830:                     table.Cells[row + 1, column].TextString = values[row, column];
00831:             }
00832:             for (int row = 0; row < table.Rows.Count; row++)
00833:             {
00834:                 for (int column = 0; column < table.Columns.Count; column++)
00835:                 {
00836:                     table.Cells[row, column].TextHeight = textHeight * 0.78;
00837:                     table.Cells[row, column].Alignment = column % 2 == 0
00838:                         ? CellAlignment.MiddleCenter
00839:                         : CellAlignment.MiddleLeft;
00840:                 }
00841:             }
00842:             table.ColorIndex = 8;
00843:             return table;
00844:         }
00845: 
00846:         private static Table BuildTable(
00847:             Database database,
00848:             Point3d position,
00849:             string title,
00850:             string[] headings,
```

### Lines 1411-1658
```csharp
01411:             if (selection == ClientPaperSelection.A4 || selection == ClientPaperSelection.Both)
01412:                 pages.AddRange(PagesForPaper("A4", 297.0, 210.0));
01413:             if (selection == ClientPaperSelection.A3 || selection == ClientPaperSelection.Both)
01414:                 pages.AddRange(PagesForPaper("A3", 420.0, 297.0));
01415:             return pages;
01416:         }
01417: 
01418:         private static List<ClientPageDefinition> PagesForPaper(
01419:             string paper,
01420:             double width,
01421:             double height)
01422:         {
01423:             return new List<ClientPageDefinition>
01424:             {
01425:                 Page(paper, "COVER", "00", "Cover and Issue Information", ClientPageKind.Cover, width, height),
01426:                 Page(paper, "PROJECT", "01", "Project Summary", ClientPageKind.ProjectSummary, width, height),
01427:                 Page(paper, "DESIGN", "02", "Design Discipline Summary", ClientPageKind.DesignSummary, width, height),
01428:                 Page(paper, "QUANTITIES", "03", "Quantity Summary", ClientPageKind.QuantitySummary, width, height),
01429:                 Page(paper, "DRAWINGS", "04", "Drawing Register", ClientPageKind.DrawingRegister, width, height),
01430:                 Page(paper, "SECTIONS", "05", "Cross-Section Register", ClientPageKind.SectionRegister, width, height),
01431:                 Page(paper, "DETAILS", "06", "Typical Detail Schedule", ClientPageKind.TypicalDetails, width, height)
01432:             };
01433:         }
01434: 
01435:         private static ClientPageDefinition Page(
01436:             string paper,
01437:             string key,
01438:             string number,
01439:             string title,
01440:             ClientPageKind kind,
01441:             double width,
01442:             double height)
01443:         {
01444:             return new ClientPageDefinition(
01445:                 "CE-CLIENT-" + paper + "-" + number + "-" + key,
01446:                 paper,
01447:                 key,
01448:                 number,
01449:                 title,
01450:                 kind,
01451:                 width,
01452:                 height);
01453:         }
01454: 
01455:         private static ClientPageDefinition FindPageDefinition(string paper, string key)
01456:         {
01457:             return PageDefinitions(ClientPaperSelection.Both).FirstOrDefault(
01458:                 item => Equal(item.Paper, paper) && Equal(item.PageKey, key));
01459:         }
01460: 
01461:         private static List<TypicalDetail> TypicalDetails()
01462:         {
01463:             return new List<TypicalDetail>
01464:             {
01465:                 new TypicalDetail("Railway Track Section", "Rail / Transport", "railway"),
01466:                 new TypicalDetail("Airport Runway Layout and Sections", "Airport / Transport", "runway"),
01467:                 new TypicalDetail("Airport Taxiway Layout and Sections", "Airport / Transport", "taxiway"),
01468:                 new TypicalDetail("Roundabout Layout and Sections", "Road", "roundabout"),
01469:                 new TypicalDetail("RCC Bridge Deck", "Bridge", "bridge"),
01470:                 new TypicalDetail("Bridge General Arrangement", "Bridge", "bridge"),
01471:                 new TypicalDetail("RCC Box Culvert Assembly", "Stormwater", "culvert"),
01472:                 new TypicalDetail("Valve Assembly Details", "Water", "water"),
01473:                 new TypicalDetail("Parking Plan and Bay Details", "Site / Road", "parking"),
01474:                 new TypicalDetail("Traffic Island Detail", "Road", "traffic island"),
01475:                 new TypicalDetail("Kerb Stone Detail", "Road", "kerb"),
01476:                 new TypicalDetail("Side Drain Detail", "Stormwater", "side drain"),
01477:                 new TypicalDetail("Headwall and Wingwall Detail", "Stormwater", "headwall"),
01478:                 new TypicalDetail("Stormwater Drain / UHI Detail", "Stormwater", "storm"),
01479:                 new TypicalDetail("Manhole Detail", "Sewer", "sewer"),
01480:                 new TypicalDetail("Inspection Chamber Detail", "Sewer", "sewer"),
01481:                 new TypicalDetail("Underground Water Tank Detail", "Water / Bulk Water", "water tank")
01482:             };
01483:         }
01484: 
01485:         private static string DetailStatus(ClientSnapshot snapshot, TypicalDetail detail)
01486:         {
01487:             bool relevant = snapshot.Groups.Any(
01488:                 group => ContainsAny(
01489:                     group.Discipline + " " + group.Layer + " " + group.TypeName,
01490:                     detail.Keyword));
01491:             return relevant ? "Suggested - insert approved DWG" : "Library reference";
01492:         }
01493: 
01494:         private static string PageNote(ClientPageKind kind)
01495:         {
01496:             if (kind == ClientPageKind.QuantitySummary)
01497:                 return "Quantities are an inventory summary, not a certified payment BOQ. Refresh the linked BOQ before issue.";
01498:             if (kind == ClientPageKind.TypicalDetails)
01499:                 return "Use only office-approved, engineer-reviewed DWG detail blocks. Reference images and example dimensions are not design authority.";
01500:             if (kind == ClientPageKind.SectionRegister)
01501:                 return "Linked sections are current at the last CE Tools refresh. Confirm section labels, levels and scales before issue.";
01502:             return "This sheet is generated from the current CE Tools project snapshot. Run CE_CLIENTBOOKREFRESH before every client issue.";
01503:         }
01504: 
01505:         private static bool PromptPaperSelection(
01506:             Editor editor,
01507:             out ClientPaperSelection selection)
01508:         {
01509:             var options = new PromptKeywordOptions(
01510:                 "\nClient book paper [A4/A3/Both] <Both>: ")
01511:             {
01512:                 AllowNone = true
01513:             };
01514:             options.Keywords.Add("A4");
01515:             options.Keywords.Add("A3");
01516:             options.Keywords.Add("Both");
01517:             PromptResult result = editor.GetKeywords(options);
01518:             if (result.Status == PromptStatus.Cancel)
01519:             {
01520:                 selection = ClientPaperSelection.Both;
01521:                 return false;
01522:             }
01523:             string value = result.Status == PromptStatus.None ? "Both" : result.StringResult;
01524:             return Enum.TryParse(value, true, out selection);
01525:         }
01526: 
01527:         private static bool PromptStage(Editor editor, out string stage)
01528:         {
01529:             var options = new PromptKeywordOptions(
01530:                 "\nProject issue stage [Concept/Preliminary/Tender/Construction/AsBuilt] <Preliminary>: ")
01531:             {
01532:                 AllowNone = true
01533:             };
01534:             foreach (string keyword in new[]
01535:             {
01536:                 "Concept", "Preliminary", "Tender", "Construction", "AsBuilt"
01537:             })
01538:                 options.Keywords.Add(keyword);
01539:             PromptResult result = editor.GetKeywords(options);
01540:             if (result.Status == PromptStatus.Cancel)
01541:             {
01542:                 stage = string.Empty;
01543:                 return false;
01544:             }
01545:             stage = result.Status == PromptStatus.None ? "Preliminary" : result.StringResult;
01546:             if (Equal(stage, "AsBuilt")) stage = "As Built";
01547:             return true;
01548:         }
01549: 
01550:         private static bool PromptRevision(Editor editor, out string revision)
01551:         {
01552:             var options = new PromptStringOptions(
01553:                 "\nClient-book revision <0>: ")
01554:             {
01555:                 AllowSpaces = true,
01556:                 UseDefaultValue = true,
01557:                 DefaultValue = "0"
01558:             };
01559:             PromptResult result = editor.GetString(options);
01560:             if (result.Status != PromptStatus.OK)
01561:             {
01562:                 revision = string.Empty;
01563:                 return false;
01564:             }
01565:             revision = string.IsNullOrWhiteSpace(result.StringResult)
01566:                 ? "0"
01567:                 : result.StringResult.Trim();
01568:             return true;
01569:         }
01570: 
01571:         private static bool PromptExcelPath(
01572:             Editor editor,
01573:             string defaultName,
01574:             out string path)
01575:         {
01576:             var options = new PromptSaveFileOptions(
01577:                 "\nSelect client-book index workbook output path: ")
01578:             {
01579:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
01580:                 DialogCaption = "Export CE Tools Client Book Index",
01581:                 InitialFileName = defaultName
01582:             };
01583:             PromptFileNameResult result = editor.GetFileNameForSave(options);
01584:             if (result.Status != PromptStatus.OK)
01585:             {
01586:                 path = string.Empty;
01587:                 return false;
01588:             }
01589:             path = result.StringResult;
01590:             if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
01591:                 path += ".xlsx";
01592:             return true;
01593:         }
01594: 
01595:         private static bool Confirm(Editor editor, string message)
01596:         {
01597:             var options = new PromptKeywordOptions(
01598:                 "\n" + message + "? [Yes/No] <No>: ")
01599:             {
01600:                 AllowNone = true
01601:             };
01602:             options.Keywords.Add("Yes");
01603:             options.Keywords.Add("No");
01604:             PromptResult result = editor.GetKeywords(options);
01605:             return result.Status == PromptStatus.OK && Equal(result.StringResult, "Yes");
01606:         }
01607: 
01608:         private static void WriteSnapshotPreview(Editor editor, ClientSnapshot snapshot)
01609:         {
01610:             editor.WriteMessage(
01611:                 "\nCE Tools client-book snapshot. Project={0}; report groups={1}; linked BOQs={2}; linked sections={3}; layouts={4}; rejected={5}.",
01612:                 ValueOrNotSet(snapshot.Project.Get("Project Name")),
01613:                 snapshot.Groups.Count,
01614:                 snapshot.LinkedBoqCount,
01615:                 snapshot.Sections.Count,
01616:                 snapshot.Layouts.Count,
01617:                 snapshot.Rejected);
01618:         }
01619: 
01620:         private static string JoinLocation(string town, string country)
01621:         {
01622:             var values = new List<string>();
01623:             if (!string.IsNullOrWhiteSpace(town)) values.Add(town.Trim());
01624:             if (!string.IsNullOrWhiteSpace(country)) values.Add(country.Trim());
01625:             return values.Count == 0 ? "<Not set>" : string.Join(", ", values);
01626:         }
01627: 
01628:         private static string FormatMeasure(double value)
01629:         {
01630:             return IsFinitePositive(value)
01631:                 ? value.ToString("N3", CultureInfo.CurrentCulture)
01632:                 : string.Empty;
01633:         }
01634: 
01635:         private static string FriendlyTypeName(string value)
01636:         {
01637:             if (string.IsNullOrWhiteSpace(value)) return "Design element";
01638:             var result = new List<char>();
01639:             for (int index = 0; index < value.Length; index++)
01640:             {
01641:                 char character = value[index];
01642:                 if (index > 0 && char.IsUpper(character) && !char.IsUpper(value[index - 1]))
01643:                     result.Add(' ');
01644:                 result.Add(character);
01645:             }
01646:             return new string(result.ToArray());
01647:         }
01648: 
01649:         private static string ValueOrNotSet(string value)
01650:         {
01651:             return string.IsNullOrWhiteSpace(value) ? "<Not set>" : value;
01652:         }
01653: 
01654:         private static string Get(IDictionary<string, string> values, string key)
01655:         {
01656:             string value;
01657:             return values.TryGetValue(key, out value) ? value : string.Empty;
01658:         }
```

### Lines 1682-1754
```csharp
01682:         private static Document ActiveDocument()
01683:         {
01684:             return AcApplication.DocumentManager.MdiActiveDocument;
01685:         }
01686: 
01687:         private enum ClientPaperSelection
01688:         {
01689:             A4,
01690:             A3,
01691:             Both
01692:         }
01693: 
01694:         private enum ClientPageKind
01695:         {
01696:             Cover,
01697:             ProjectSummary,
01698:             DesignSummary,
01699:             QuantitySummary,
01700:             DrawingRegister,
01701:             SectionRegister,
01702:             TypicalDetails
01703:         }
01704: 
01705:         private sealed class ClientPageDefinition
01706:         {
01707:             public ClientPageDefinition(
01708:                 string layoutName,
01709:                 string paper,
01710:                 string pageKey,
01711:                 string pageNumber,
01712:                 string title,
01713:                 ClientPageKind kind,
01714:                 double width,
01715:                 double height)
01716:             {
01717:                 LayoutName = layoutName;
01718:                 Paper = paper;
01719:                 PageKey = pageKey;
01720:                 PageNumber = pageNumber;
01721:                 Title = title;
01722:                 Kind = kind;
01723:                 Width = width;
01724:                 Height = height;
01725:             }
01726: 
01727:             public string LayoutName { get; }
01728:             public string Paper { get; }
01729:             public string PageKey { get; }
01730:             public string PageNumber { get; }
01731:             public string Title { get; }
01732:             public ClientPageKind Kind { get; }
01733:             public double Width { get; }
01734:             public double Height { get; }
01735:         }
01736: 
01737:         private sealed class ClientPageLink
01738:         {
01739:             public ClientPageLink(
01740:                 string schema,
01741:                 string layoutName,
01742:                 string paper,
01743:                 string pageKey,
01744:                 string pageNumber,
01745:                 string title,
01746:                 string stage,
01747:                 string revision,
01748:                 double width,
01749:                 double height,
01750:                 IEnumerable<string> generatedHandles)
01751:             {
01752:                 Schema = schema;
01753:                 LayoutName = layoutName;
01754:                 Paper = paper;
```

## ClosedParkingBayWorkflow.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 366-445
```csharp
00366:                     "\nParking divider angle must be greater than 0 and less than 180 degrees.");
00367:                 return null;
00368:             }
00369: 
00370:             double aisleWidth = 0.0;
00371:             string side = "Left";
00372:             if (includeAisle)
00373:             {
00374:                 PromptDoubleResult aisle = PromptPositiveDouble(
00375:                     editor,
00376:                     "\nEnter aisle width <6.000>: ",
00377:                     6.0);
00378:                 if (aisle.Status != PromptStatus.OK)
00379:                     return null;
00380:                 aisleWidth = aisle.Value;
00381:             }
00382:             else
00383:             {
00384:                 var sideOptions = new PromptKeywordOptions(
00385:                     "\nCreate parking bays on which side [Left/Right] <Left>: ")
00386:                 {
00387:                     AllowNone = true
00388:                 };
00389:                 sideOptions.Keywords.Add("Left");
00390:                 sideOptions.Keywords.Add("Right");
00391:                 PromptResult sideResult = editor.GetKeywords(sideOptions);
00392:                 if (sideResult.Status == PromptStatus.Cancel)
00393:                     return null;
00394:                 if (sideResult.Status == PromptStatus.OK)
00395:                     side = sideResult.StringResult;
00396:             }
00397: 
00398:             return new ParkingLayout(
00399:                 width.Value,
00400:                 depth.Value,
00401:                 angle.Value,
00402:                 aisleWidth,
00403:                 side);
00404:         }
00405: 
00406:         private static PromptDoubleResult PromptPositiveDouble(
00407:             Editor editor,
00408:             string message,
00409:             double defaultValue)
00410:         {
00411:             return editor.GetDouble(
00412:                 new PromptDoubleOptions(message)
00413:                 {
00414:                     AllowNone = true,
00415:                     AllowNegative = false,
00416:                     AllowZero = false,
00417:                     DefaultValue = defaultValue,
00418:                     UseDefaultValue = true
00419:                 });
00420:         }
00421: 
00422:         private static ObjectId CreateBayBlockDefinition(
00423:             Database database,
00424:             Transaction transaction,
00425:             double width,
00426:             double depth,
00427:             double angleDegrees)
00428:         {
00429:             BlockTable blockTable = (BlockTable)transaction.GetObject(
00430:                 database.BlockTableId,
00431:                 OpenMode.ForWrite,
00432:                 false);
00433:             var definition = new BlockTableRecord
00434:             {
00435:                 Name = "CE_PARKING_BAY_" + Guid.NewGuid().ToString("N")
00436:             };
00437:             ObjectId definitionId = blockTable.Add(definition);
00438:             transaction.AddNewlyCreatedDBObject(definition, true);
00439: 
00440:             double angle = DegreesToRadians(angleDegrees);
00441:             Vector2d depthVector = new Vector2d(
00442:                 Math.Cos(angle) * depth,
00443:                 Math.Sin(angle) * depth);
00444:             var bay = new Polyline(4);
00445:             bay.SetDatabaseDefaults(database);
```

## CommandUsageTracker.cs
Hits: `GetString(`

### Lines 625-697
```csharp
00625:         private static string StoragePath()
00626:         {
00627:             return Path.Combine(
00628:                 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
00629:                 "CE Tools",
00630:                 "CommandUsage.tsv");
00631:         }
00632: 
00633:         private static string Encode(string value)
00634:         {
00635:             return Convert.ToBase64String(
00636:                 Encoding.UTF8.GetBytes(value ?? string.Empty));
00637:         }
00638: 
00639:         private static string Decode(string value)
00640:         {
00641:             try
00642:             {
00643:                 return Encoding.UTF8.GetString(
00644:                     Convert.FromBase64String(value ?? string.Empty));
00645:             }
00646:             catch
00647:             {
00648:                 return string.Empty;
00649:             }
00650:         }
00651: 
00652:         private static string NormalizeCommand(string command)
00653:         {
00654:             return (command ?? string.Empty).Trim().ToUpperInvariant();
00655:         }
00656: 
00657:         private static void RaiseUsageChanged()
00658:         {
00659:             EventHandler handler = UsageChanged;
00660:             if (handler != null) handler(null, EventArgs.Empty);
00661:         }
00662:     }
00663: 
00664:     internal sealed class ProjectIdentity
00665:     {
00666:         public ProjectIdentity(
00667:             string key,
00668:             string displayName,
00669:             string fullName,
00670:             bool persist)
00671:         {
00672:             Key = key;
00673:             DisplayName = displayName;
00674:             FullName = fullName;
00675:             Persist = persist;
00676:         }
00677: 
00678:         public string Key { get; private set; }
00679:         public string DisplayName { get; private set; }
00680:         public string FullName { get; private set; }
00681:         public bool Persist { get; private set; }
00682:     }
00683: 
00684:     internal sealed class ProjectUsageRecord
00685:     {
00686:         public ProjectUsageRecord()
00687:         {
00688:             Commands = new Dictionary<string, CommandUsageRecord>(
00689:                 StringComparer.OrdinalIgnoreCase);
00690:         }
00691: 
00692:         public string Key { get; set; }
00693:         public string DisplayName { get; set; }
00694:         public string FullName { get; set; }
00695:         public DateTime LastOpenedUtc { get; set; }
00696:         public double ActiveSeconds { get; set; }
00697:         public bool Persist { get; set; }
```

## CoordinatePolylineCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 81-160
```csharp
00081:                 }
00082:             }
00083:             catch (System.Exception exception)
00084:             {
00085:                 editor.WriteMessage(
00086:                     "\nCE_COORDPOLY cancelled while reading the polyline: " +
00087:                     exception.Message);
00088:                 return;
00089:             }
00090: 
00091:             if (vertices.Count == 0)
00092:             {
00093:                 editor.WriteMessage(
00094:                     "\nCE_COORDPOLY cancelled. The selected polyline contains no usable vertices.");
00095:                 return;
00096:             }
00097: 
00098:             string defaultPrefix = BuildDefaultDescriptionPrefix(sourceLayer);
00099:             var descriptionOptions = new PromptStringOptions(
00100:                 "\nPoint description prefix <" + defaultPrefix + ">: ")
00101:             {
00102:                 AllowSpaces = true,
00103:                 DefaultValue = defaultPrefix,
00104:                 UseDefaultValue = true
00105:             };
00106:             PromptResult descriptionResult = editor.GetString(descriptionOptions);
00107:             if (descriptionResult.Status != PromptStatus.OK)
00108:             {
00109:                 return;
00110:             }
00111: 
00112:             string descriptionPrefix =
00113:                 (descriptionResult.StringResult ?? defaultPrefix).Trim();
00114:             if (descriptionPrefix.Length == 0)
00115:             {
00116:                 descriptionPrefix = defaultPrefix;
00117:             }
00118: 
00119:             var startOptions = new PromptIntegerOptions(
00120:                 "\nStarting description sequence number <1>: ")
00121:             {
00122:                 AllowNegative = false,
00123:                 AllowZero = false,
00124:                 DefaultValue = 1,
00125:                 LowerLimit = 1,
00126:                 UseDefaultValue = true
00127:             };
00128:             PromptIntegerResult startResult = editor.GetInteger(startOptions);
00129:             if (startResult.Status != PromptStatus.OK)
00130:             {
00131:                 return;
00132:             }
00133: 
00134:             var tablePointOptions = new PromptPointOptions(
00135:                 "\nPick insertion point for the polyline vertex XYZ table: ");
00136:             PromptPointResult tablePointResult = editor.GetPoint(tablePointOptions);
00137:             if (tablePointResult.Status != PromptStatus.OK)
00138:             {
00139:                 return;
00140:             }
00141: 
00142:             Point3d tablePoint = tablePointResult.Value.TransformBy(
00143:                 editor.CurrentUserCoordinateSystem);
00144: 
00145:             string firstDescription = FormatDescription(
00146:                 descriptionPrefix,
00147:                 startResult.Value);
00148:             string lastDescription = FormatDescription(
00149:                 descriptionPrefix,
00150:                 startResult.Value + vertices.Count - 1);
00151: 
00152:             editor.WriteMessage(
00153:                 "\nCE_COORDPOLY preview: vertices={0}; first={1}; last={2}. " +
00154:                 "Civil 3D point numbers will use the drawing's next-point-number sequence.",
00155:                 vertices.Count,
00156:                 firstDescription,
00157:                 lastDescription);
00158: 
00159:             if (!Confirm(editor, "Create the COGO points and XYZ table"))
00160:             {
```

### Lines 446-506
```csharp
00446:             {
00447:                 // Best-effort cleanup only. The command reports that cleanup
00448:                 // occurred where possible instead of hiding the original error.
00449:             }
00450:         }
00451: 
00452:         private static double GetTextHeight(Database database)
00453:         {
00454:             double textHeight = database.Textsize;
00455:             return textHeight > 0.0 &&
00456:                    !double.IsNaN(textHeight) &&
00457:                    !double.IsInfinity(textHeight)
00458:                 ? textHeight
00459:                 : 2.5;
00460:         }
00461: 
00462:         private static bool Confirm(Editor editor, string message)
00463:         {
00464:             var options = new PromptKeywordOptions(
00465:                 "\n" + message + "? [Yes/No] <No>: ")
00466:             {
00467:                 AllowNone = true
00468:             };
00469:             options.Keywords.Add("Yes");
00470:             options.Keywords.Add("No");
00471: 
00472:             PromptResult result = editor.GetKeywords(options);
00473:             return result.Status == PromptStatus.OK &&
00474:                    string.Equals(
00475:                        result.StringResult,
00476:                        "Yes",
00477:                        StringComparison.OrdinalIgnoreCase);
00478:         }
00479: 
00480:         private sealed class CoordinateRecord
00481:         {
00482:             public CoordinateRecord(
00483:                 string pointNumber,
00484:                 string description,
00485:                 double x,
00486:                 double y,
00487:                 double z,
00488:                 int vertexNumber)
00489:             {
00490:                 PointNumber = pointNumber;
00491:                 Description = description;
00492:                 X = x;
00493:                 Y = y;
00494:                 Z = z;
00495:                 VertexNumber = vertexNumber;
00496:             }
00497: 
00498:             public string PointNumber { get; }
00499:             public string Description { get; }
00500:             public double X { get; }
00501:             public double Y { get; }
00502:             public double Z { get; }
00503:             public int VertexNumber { get; }
00504:         }
00505:     }
00506: }
```

## CoordinateSystemCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 143-278
```csharp
00143:                     drawingSettings.ApplyTransformSettings ? "Yes" : "No");
00144:             }
00145:             catch (System.Exception exception)
00146:             {
00147:                 editor.WriteMessage("\nCE_COORDSYSINFO cancelled. {0}", exception.Message);
00148:             }
00149:         }
00150: 
00151:         private static void AssignCoordinateSystemByCode(Document document)
00152:         {
00153:             Editor editor = document.Editor;
00154:             CivilDocument civilDocument = CivilApplication.ActiveDocument;
00155:             if (civilDocument == null)
00156:             {
00157:                 editor.WriteMessage("\nCE_COORDSYSCODE cancelled. No active Civil 3D document is available.");
00158:                 return;
00159:             }
00160: 
00161:             PromptResult codeResult = editor.GetString(
00162:                 new PromptStringOptions(
00163:                     "\nEnter Autodesk coordinate-system code, or use CE_COORDSYSASSIGN for the native selection window: ")
00164:                 {
00165:                     AllowSpaces = false
00166:                 });
00167:             if (codeResult.Status != PromptStatus.OK)
00168:             {
00169:                 return;
00170:             }
00171: 
00172:             string requestedCode = FindCanonicalCode(codeResult.StringResult);
00173:             if (string.IsNullOrWhiteSpace(requestedCode) ||
00174:                 IsNoCoordinateSystem(requestedCode))
00175:             {
00176:                 editor.WriteMessage(
00177:                     "\nCE_COORDSYSCODE cancelled. The entered coordinate-system code is not valid. " +
00178:                     "Use CE_COORDSYSSEARCH or CE_COORDSYSASSIGN to select an available code.");
00179:                 return;
00180:             }
00181: 
00182:             SettingsUnitZone unitZone = civilDocument.Settings.DrawingSettings.UnitZoneSettings;
00183:             string originalCode = NormalizeCode(unitZone.CoordinateSystemCode);
00184: 
00185:             if (string.Equals(originalCode, requestedCode, StringComparison.OrdinalIgnoreCase))
00186:             {
00187:                 editor.WriteMessage("\nCE_COORDSYSCODE: {0} is already assigned.", requestedCode);
00188:                 WriteCoordinateSystemDetails(editor, requestedCode, "Current system");
00189:                 return;
00190:             }
00191: 
00192:             editor.WriteMessage("\nCoordinate-system assignment preview");
00193:             WriteCoordinateSystemDetails(editor, originalCode, "Current system");
00194:             WriteCoordinateSystemDetails(editor, requestedCode, "Proposed system");
00195:             editor.WriteMessage(
00196:                 "\n  WARNING: Assigning a coordinate system does not move, rotate, scale or transform existing geometry.");
00197: 
00198:             if (!Confirm(editor, "Assign the proposed coordinate system"))
00199:             {
00200:                 editor.WriteMessage("\nCE_COORDSYSCODE cancelled. The drawing coordinate system was not changed.");
00201:                 return;
00202:             }
00203: 
00204:             try
00205:             {
00206:                 unitZone.CoordinateSystemCode = requestedCode;
00207:                 editor.WriteMessage(
00208:                     "\nCE_COORDSYSCODE complete. Drawing coordinate system assigned: {0}.",
00209:                     requestedCode);
00210:             }
00211:             catch (System.Exception exception)
00212:             {
00213:                 TryRestoreCode(unitZone, originalCode);
00214:                 editor.WriteMessage(
00215:                     "\nCE_COORDSYSCODE cancelled. The original coordinate system was retained where possible. {0}",
00216:                     exception.Message);
00217:             }
00218:         }
00219: 
00220:         private static void SearchCoordinateSystems(Document document)
00221:         {
00222:             Editor editor = document.Editor;
00223:             PromptResult searchResult = editor.GetString(
00224:                 new PromptStringOptions(
00225:                     "\nSearch coordinate systems by code, description, category, projection or datum: ")
00226:                 {
00227:                     AllowSpaces = true
00228:                 });
00229:             if (searchResult.Status != PromptStatus.OK)
00230:             {
00231:                 return;
00232:             }
00233: 
00234:             string searchText = (searchResult.StringResult ?? string.Empty).Trim();
00235:             if (searchText.Length == 0)
00236:             {
00237:                 editor.WriteMessage("\nCE_COORDSYSSEARCH cancelled. Enter search text.");
00238:                 return;
00239:             }
00240: 
00241:             string[] codes;
00242:             try
00243:             {
00244:                 codes = SettingsUnitZone.GetAllCodes();
00245:             }
00246:             catch (System.Exception exception)
00247:             {
00248:                 editor.WriteMessage("\nCE_COORDSYSSEARCH cancelled. {0}", exception.Message);
00249:                 return;
00250:             }
00251: 
00252:             Array.Sort(codes, StringComparer.OrdinalIgnoreCase);
00253:             var matches = new List<CoordinateSystemSummary>();
00254:             int totalMatches = 0;
00255: 
00256:             foreach (string code in codes)
00257:             {
00258:                 if (IsNoCoordinateSystem(code))
00259:                 {
00260:                     continue;
00261:                 }
00262: 
00263:                 SettingsCoordinateSystem coordinateSystem;
00264:                 try
00265:                 {
00266:                     coordinateSystem = SettingsUnitZone.GetCoordinateSystemByCode(code);
00267:                 }
00268:                 catch
00269:                 {
00270:                     continue;
00271:                 }
00272: 
00273:                 if (!Matches(coordinateSystem, searchText))
00274:                 {
00275:                     continue;
00276:                 }
00277: 
00278:                 totalMatches++;
```

### Lines 428-508
```csharp
00428:                     ValueOrNotSet(coordinateSystem.Description),
00429:                     ValueOrNotSet(coordinateSystem.Category),
00430:                     ValueOrNotSet(coordinateSystem.Projection),
00431:                     ValueOrNotSet(coordinateSystem.Datum),
00432:                     ValueOrNotSet(coordinateSystem.Unit));
00433:             }
00434:             catch (System.Exception exception)
00435:             {
00436:                 editor.WriteMessage(
00437:                     "\n  {0}: {1}; details unavailable: {2}",
00438:                     heading,
00439:                     code,
00440:                     exception.Message);
00441:             }
00442:         }
00443: 
00444:         private static bool Confirm(Editor editor, string message)
00445:         {
00446:             var options = new PromptKeywordOptions(
00447:                 "\n" + message + "? [Yes/No] <No>: ")
00448:             {
00449:                 AllowNone = true
00450:             };
00451:             options.Keywords.Add("Yes");
00452:             options.Keywords.Add("No");
00453: 
00454:             PromptResult result = editor.GetKeywords(options);
00455:             return result.Status == PromptStatus.OK &&
00456:                    string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00457:         }
00458: 
00459:         private static void TryRestoreCode(SettingsUnitZone unitZone, string originalCode)
00460:         {
00461:             try
00462:             {
00463:                 unitZone.CoordinateSystemCode = IsNoCoordinateSystem(originalCode)
00464:                     ? NoCoordinateSystemCode
00465:                     : originalCode;
00466:             }
00467:             catch
00468:             {
00469:                 // The original exception is more useful to the user than a restore failure.
00470:             }
00471:         }
00472: 
00473:         private static string NormalizeCode(string code)
00474:         {
00475:             string value = (code ?? string.Empty).Trim();
00476:             return value.Length == 0 ? NoCoordinateSystemCode : value;
00477:         }
00478: 
00479:         private static bool IsNoCoordinateSystem(string code)
00480:         {
00481:             string value = (code ?? string.Empty).Trim();
00482:             return value.Length == 0 || value == NoCoordinateSystemCode;
00483:         }
00484: 
00485:         private static string ValueOrNotSet(object value)
00486:         {
00487:             string text = value == null ? string.Empty : value.ToString();
00488:             return string.IsNullOrWhiteSpace(text) ? "<Not set>" : text;
00489:         }
00490: 
00491:         private sealed class CoordinateSystemSummary
00492:         {
00493:             public CoordinateSystemSummary(SettingsCoordinateSystem coordinateSystem)
00494:             {
00495:                 Code = coordinateSystem.Code;
00496:                 Description = ValueOrNotSet(coordinateSystem.Description);
00497:                 Category = ValueOrNotSet(coordinateSystem.Category);
00498:                 Projection = ValueOrNotSet(coordinateSystem.Projection);
00499:                 Datum = ValueOrNotSet(coordinateSystem.Datum);
00500:                 Unit = ValueOrNotSet(coordinateSystem.Unit);
00501:             }
00502: 
00503:             public string Code { get; }
00504: 
00505:             public string Description { get; }
00506: 
00507:             public string Category { get; }
00508: 
```

## CorridorCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 23-104
```csharp
00023:     public sealed class CorridorCommands
00024:     {
00025:         private const string ReportKeyword = "Report";
00026:         private const string BaselinesKeyword = "Baselines";
00027:         private const string RebuildKeyword = "Rebuild";
00028: 
00029:         [CommandMethod(
00030:             "CE_TOOLS",
00031:             "CE_CORTOOLS",
00032:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00033:         public void CorridorTools()
00034:         {
00035:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00036:             if (document == null)
00037:             {
00038:                 return;
00039:             }
00040: 
00041:             var options = new PromptKeywordOptions(
00042:                 "\nCorridor tool [Report/Baselines/Rebuild] <Report>: ")
00043:             {
00044:                 AllowNone = true
00045:             };
00046:             options.Keywords.Add(ReportKeyword);
00047:             options.Keywords.Add(BaselinesKeyword);
00048:             options.Keywords.Add(RebuildKeyword);
00049: 
00050:             PromptResult result = document.Editor.GetKeywords(options);
00051:             if (result.Status == PromptStatus.Cancel)
00052:             {
00053:                 return;
00054:             }
00055: 
00056:             string mode = result.Status == PromptStatus.None
00057:                 ? ReportKeyword
00058:                 : result.StringResult;
00059: 
00060:             if (string.Equals(mode, BaselinesKeyword, StringComparison.OrdinalIgnoreCase))
00061:             {
00062:                 ReportBaselines(document);
00063:             }
00064:             else if (string.Equals(mode, RebuildKeyword, StringComparison.OrdinalIgnoreCase))
00065:             {
00066:                 RebuildCorridors(document);
00067:             }
00068:             else
00069:             {
00070:                 ReportCorridors(document);
00071:             }
00072:         }
00073: 
00074:         [CommandMethod(
00075:             "CE_TOOLS",
00076:             "CE_CORREPORT",
00077:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00078:         public void CorridorReport()
00079:         {
00080:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00081:             if (document != null)
00082:             {
00083:                 ReportCorridors(document);
00084:             }
00085:         }
00086: 
00087:         [CommandMethod(
00088:             "CE_TOOLS",
00089:             "CE_CORBASE",
00090:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00091:         public void CorridorBaselineReport()
00092:         {
00093:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00094:             if (document != null)
00095:             {
00096:                 ReportBaselines(document);
00097:             }
00098:         }
00099: 
00100:         [CommandMethod(
00101:             "CE_TOOLS",
00102:             "CE_CORREBUILD",
00103:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00104:         public void CorridorRebuild()
```

### Lines 266-350
```csharp
00266:                 editor,
00267:                 "\nSelect Civil 3D corridors to rebuild: ");
00268:             if (selection.Status != PromptStatus.OK)
00269:             {
00270:                 return;
00271:             }
00272: 
00273:             RebuildPreview preview = BuildRebuildPreview(document.Database, selection);
00274:             if (preview.Rebuildable == 0)
00275:             {
00276:                 editor.WriteMessage(
00277:                     "\nCE_CORREBUILD preview: no editable out-of-date corridors found. " +
00278:                     "Up-to-date={0}; skipped={1}.",
00279:                     preview.UpToDate,
00280:                     preview.Skipped);
00281:                 return;
00282:             }
00283: 
00284:             var confirmOptions = new PromptKeywordOptions(
00285:                 string.Format(
00286:                     "\nRebuild {0} out-of-date corridors? Up-to-date={1}; skipped={2}. [Yes/No] <No>: ",
00287:                     preview.Rebuildable,
00288:                     preview.UpToDate,
00289:                     preview.Skipped))
00290:             {
00291:                 AllowNone = true
00292:             };
00293:             confirmOptions.Keywords.Add("Yes");
00294:             confirmOptions.Keywords.Add("No");
00295: 
00296:             PromptResult confirmResult = editor.GetKeywords(confirmOptions);
00297:             if (confirmResult.Status != PromptStatus.OK ||
00298:                 !string.Equals(confirmResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
00299:             {
00300:                 editor.WriteMessage("\nCE_CORREBUILD cancelled. No corridors were rebuilt.");
00301:                 return;
00302:             }
00303: 
00304:             int rebuilt = 0;
00305:             int skipped = 0;
00306: 
00307:             try
00308:             {
00309:                 using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00310:                 {
00311:                     foreach (SelectedObject selectedObject in selection.Value)
00312:                     {
00313:                         CivilCorridor corridor = OpenCorridor(
00314:                             transaction,
00315:                             selectedObject == null ? ObjectId.Null : selectedObject.ObjectId,
00316:                             OpenMode.ForWrite);
00317: 
00318:                         if (corridor == null ||
00319:                             corridor.IsReferenceObject ||
00320:                             IsLayerLocked(transaction, corridor.LayerId) ||
00321:                             !corridor.IsOutOfDate)
00322:                         {
00323:                             skipped++;
00324:                             continue;
00325:                         }
00326: 
00327:                         corridor.Rebuild();
00328:                         rebuilt++;
00329:                     }
00330: 
00331:                     transaction.Commit();
00332:                 }
00333: 
00334:                 editor.WriteMessage(
00335:                     "\nCE_CORREBUILD complete. Corridors rebuilt={0}; skipped={1}.",
00336:                     rebuilt,
00337:                     skipped);
00338:             }
00339:             catch (System.Exception exception)
00340:             {
00341:                 editor.WriteMessage(
00342:                     "\nCE_CORREBUILD cancelled. No rebuild changes were committed. {0}",
00343:                     exception.Message);
00344:             }
00345:         }
00346: 
00347:         private static RebuildPreview BuildRebuildPreview(
00348:             Database database,
00349:             PromptSelectionResult selection)
00350:         {
```

## DesignStandardsLibraryCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 198-434
```csharp
00198:                     "International",
00199:                     "General",
00200:                     "Project-specific international standards",
00201:                     "Client / approving authority",
00202:                     "Record the exact issuing body, standard number, edition and local adoption requirements.",
00203:                     "Project contract and issuing organisation",
00204:                     "aashto; eurocode; british standard; iso; international; project specific")
00205:             };
00206: 
00207:         [CommandMethod("CE_TOOLS", "CE_DESIGNSTANDARDS", CommandFlags.Modal | CommandFlags.Redraw)]
00208:         public void DesignStandardsMenu()
00209:         {
00210:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00211:             if (document == null)
00212:             {
00213:                 return;
00214:             }
00215: 
00216:             var options = new PromptKeywordOptions(
00217:                 "\nCE Design Standards [Browse/Search/Apply/Current] <Browse>: ")
00218:             {
00219:                 AllowNone = true
00220:             };
00221:             options.Keywords.Add("Browse");
00222:             options.Keywords.Add("Search");
00223:             options.Keywords.Add("Apply");
00224:             options.Keywords.Add("Current");
00225: 
00226:             PromptResult result = document.Editor.GetKeywords(options);
00227:             if (result.Status == PromptStatus.Cancel)
00228:             {
00229:                 return;
00230:             }
00231: 
00232:             string mode = result.Status == PromptStatus.None
00233:                 ? "Browse"
00234:                 : result.StringResult;
00235: 
00236:             if (string.Equals(mode, "Search", StringComparison.OrdinalIgnoreCase))
00237:             {
00238:                 Search(document);
00239:             }
00240:             else if (string.Equals(mode, "Apply", StringComparison.OrdinalIgnoreCase))
00241:             {
00242:                 Apply(document);
00243:             }
00244:             else if (string.Equals(mode, "Current", StringComparison.OrdinalIgnoreCase))
00245:             {
00246:                 ReportCurrent(document);
00247:             }
00248:             else
00249:             {
00250:                 Browse(document);
00251:             }
00252:         }
00253: 
00254:         [CommandMethod("CE_TOOLS", "CE_STDBROWSE", CommandFlags.Modal)]
00255:         public void BrowseCommand()
00256:         {
00257:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00258:             if (document != null)
00259:             {
00260:                 Browse(document);
00261:             }
00262:         }
00263: 
00264:         [CommandMethod("CE_TOOLS", "CE_STDSEARCH", CommandFlags.Modal)]
00265:         public void SearchCommand()
00266:         {
00267:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00268:             if (document != null)
00269:             {
00270:                 Search(document);
00271:             }
00272:         }
00273: 
00274:         [CommandMethod("CE_TOOLS", "CE_STDAPPLY", CommandFlags.Modal | CommandFlags.Redraw)]
00275:         public void ApplyCommand()
00276:         {
00277:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00278:             if (document != null)
00279:             {
00280:                 Apply(document);
00281:             }
00282:         }
00283: 
00284:         private static void Browse(Document document)
00285:         {
00286:             Editor editor = document.Editor;
00287:             var options = new PromptKeywordOptions(
00288:                 "\nStandards category [Namibia/Roads/Pavement/Drainage/Settlements/General/All] <All>: ")
00289:             {
00290:                 AllowNone = true
00291:             };
00292:             options.Keywords.Add("Namibia");
00293:             options.Keywords.Add("Roads");
00294:             options.Keywords.Add("Pavement");
00295:             options.Keywords.Add("Drainage");
00296:             options.Keywords.Add("Settlements");
00297:             options.Keywords.Add("General");
00298:             options.Keywords.Add("All");
00299: 
00300:             PromptResult result = editor.GetKeywords(options);
00301:             if (result.Status == PromptStatus.Cancel)
00302:             {
00303:                 return;
00304:             }
00305: 
00306:             string category = result.Status == PromptStatus.None
00307:                 ? "All"
00308:                 : result.StringResult;
00309:             IEnumerable<StandardEntry> matches = FilterCategory(category);
00310:             WriteEntries(editor, matches, "CE Design Standards Library - " + category);
00311:         }
00312: 
00313:         private static void Search(Document document)
00314:         {
00315:             var options = new PromptStringOptions(
00316:                 "\nSearch by code, title, discipline, authority or keyword: ")
00317:             {
00318:                 AllowSpaces = true
00319:             };
00320:             PromptResult result = document.Editor.GetString(options);
00321:             if (result.Status != PromptStatus.OK)
00322:             {
00323:                 return;
00324:             }
00325: 
00326:             string query = (result.StringResult ?? string.Empty).Trim();
00327:             if (query.Length == 0)
00328:             {
00329:                 document.Editor.WriteMessage("\nCE_STDSEARCH: enter at least one search term.");
00330:                 return;
00331:             }
00332: 
00333:             string[] terms = query
00334:                 .Split(new[] { ' ', ',', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
00335:             IEnumerable<StandardEntry> matches = Catalogue
00336:                 .Where(entry => terms.All(entry.Contains))
00337:                 .OrderBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase);
00338:             WriteEntries(document.Editor, matches, "CE Standards search: " + query);
00339:         }
00340: 
00341:         private static void Apply(Document document)
00342:         {
00343:             StandardsMetadata existing = ReadStandards(document.Database);
00344:             Editor editor = document.Editor;
00345:             editor.WriteMessage(
00346:                 "\nEnter a catalogue code such as NAM-RA, COTO-2020, TRH4, TRH17, REDBOOK or SANS-CIVIL.");
00347: 
00348:             var codeOptions = new PromptStringOptions("\nStandards catalogue code: ")
00349:             {
00350:                 AllowSpaces = false
00351:             };
00352:             PromptResult codeResult = editor.GetString(codeOptions);
00353:             if (codeResult.Status != PromptStatus.OK)
00354:             {
00355:                 return;
00356:             }
00357: 
00358:             string code = (codeResult.StringResult ?? string.Empty).Trim();
00359:             StandardEntry entry = Catalogue.FirstOrDefault(
00360:                 item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
00361:             if (entry == null)
00362:             {
00363:                 editor.WriteMessage(
00364:                     "\nCE_STDAPPLY: code '{0}' was not found. Run CE_STDBROWSE or CE_STDSEARCH.",
00365:                     code);
00366:                 return;
00367:             }
00368: 
00369:             string defaultMode = existing.Exists &&
00370:                                  !string.IsNullOrWhiteSpace(existing.Get("Primary Standard"))
00371:                 ? "Additional"
00372:                 : "Primary";
00373:             var modeOptions = new PromptKeywordOptions(
00374:                 "\nApply as [Primary/Additional] <" + defaultMode + ">: ")
00375:             {
00376:                 AllowNone = true
00377:             };
00378:             modeOptions.Keywords.Add("Primary");
00379:             modeOptions.Keywords.Add("Additional");
00380:             PromptResult modeResult = editor.GetKeywords(modeOptions);
00381:             if (modeResult.Status == PromptStatus.Cancel)
00382:             {
00383:                 return;
00384:             }
00385:             string mode = modeResult.Status == PromptStatus.None
00386:                 ? defaultMode
00387:                 : modeResult.StringResult;
00388: 
00389:             StandardsMetadata proposed = existing.Clone();
00390:             proposed.Exists = true;
00391:             if (string.IsNullOrWhiteSpace(proposed.Get("Region / Framework")) ||
00392:                 string.Equals(mode, "Primary", StringComparison.OrdinalIgnoreCase))
00393:             {
00394:                 proposed.Set("Region / Framework", entry.Region);
00395:             }
00396:             if (string.IsNullOrWhiteSpace(proposed.Get("Design Discipline")) ||
00397:                 string.Equals(mode, "Primary", StringComparison.OrdinalIgnoreCase))
00398:             {
00399:                 proposed.Set("Design Discipline", entry.Discipline);
00400:             }
00401: 
00402:             string label = entry.Code + " - " + entry.Title;
00403:             if (string.Equals(mode, "Primary", StringComparison.OrdinalIgnoreCase))
00404:             {
00405:                 proposed.Set("Primary Standard", label);
00406:                 proposed.Set("Edition / Revision", entry.EditionNote);
00407:                 proposed.Set("Approval Authority", entry.Authority);
00408:             }
00409:             else
00410:             {
00411:                 proposed.Set(
00412:                     "Additional Standards",
00413:                     AppendUnique(proposed.Get("Additional Standards"), label));
00414:                 if (string.IsNullOrWhiteSpace(proposed.Get("Approval Authority")))
00415:                 {
00416:                     proposed.Set("Approval Authority", entry.Authority);
00417:                 }
00418:             }
00419: 
00420:             proposed.Set(
00421:                 "Notes",
00422:                 AppendNote(
00423:                     proposed.Get("Notes"),
00424:                     "Catalogue source: " + entry.Source + ". " +
00425:                     "Verify the current contract, authority adoption, amendments and edition before design."));
00426:             proposed.Set("Selection Date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
00427: 
00428:             editor.WriteMessage("\nCE_STDAPPLY preview");
00429:             WriteStandards(editor, proposed);
00430:             editor.WriteMessage(
00431:                 "\n  IMPORTANT: The library is a design-basis aid only. It does not verify licensing, edition, adoption or compliance.");
00432: 
00433:             if (!Confirm(editor, "Save this standards selection inside the DWG"))
00434:             {
```

### Lines 796-875
```csharp
00796:             return string.Join("; ", values.ToArray());
00797:         }
00798: 
00799:         private static string AppendNote(string existing, string note)
00800:         {
00801:             if (string.IsNullOrWhiteSpace(existing))
00802:             {
00803:                 return note;
00804:             }
00805:             if (existing.IndexOf(note, StringComparison.OrdinalIgnoreCase) >= 0)
00806:             {
00807:                 return existing;
00808:             }
00809:             return existing.Trim() + " | " + note;
00810:         }
00811: 
00812:         private static bool Confirm(Editor editor, string message)
00813:         {
00814:             var options = new PromptKeywordOptions(
00815:                 "\n" + message + "? [Yes/No] <No>: ")
00816:             {
00817:                 AllowNone = true
00818:             };
00819:             options.Keywords.Add("Yes");
00820:             options.Keywords.Add("No");
00821:             PromptResult result = editor.GetKeywords(options);
00822:             return result.Status == PromptStatus.OK &&
00823:                    string.Equals(
00824:                        result.StringResult,
00825:                        "Yes",
00826:                        StringComparison.OrdinalIgnoreCase);
00827:         }
00828: 
00829:         private sealed class StandardEntry
00830:         {
00831:             public StandardEntry(
00832:                 string code,
00833:                 string region,
00834:                 string discipline,
00835:                 string title,
00836:                 string authority,
00837:                 string editionNote,
00838:                 string source,
00839:                 string keywords)
00840:             {
00841:                 Code = code;
00842:                 Region = region;
00843:                 Discipline = discipline;
00844:                 Title = title;
00845:                 Authority = authority;
00846:                 EditionNote = editionNote;
00847:                 Source = source;
00848:                 Keywords = keywords;
00849:             }
00850: 
00851:             public string Code { get; }
00852:             public string Region { get; }
00853:             public string Discipline { get; }
00854:             public string Title { get; }
00855:             public string Authority { get; }
00856:             public string EditionNote { get; }
00857:             public string Source { get; }
00858:             public string Keywords { get; }
00859: 
00860:             public bool Contains(string value)
00861:             {
00862:                 if (string.IsNullOrWhiteSpace(value))
00863:                 {
00864:                     return true;
00865:                 }
00866:                 string searchable = string.Join(
00867:                     " ",
00868:                     new[]
00869:                     {
00870:                         Code,
00871:                         Region,
00872:                         Discipline,
00873:                         Title,
00874:                         Authority,
00875:                         EditionNote,
```

## DetailedSectionAnnotationCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 16-97
```csharp
00016:     /// <summary>
00017:     /// Creates linked, reversible detailed-section annotations for road, parking,
00018:     /// stormwater, sewer and water section linework. Source geometry is never
00019:     /// modified. Generated dimensions, labels and the component register can be
00020:     /// refreshed or cleared as one linked set.
00021:     /// </summary>
00022:     public sealed class DetailedSectionAnnotationCommands
00023:     {
00024:         private const string RegAppName = "CE_SECTION_DETAIL";
00025:         private const string AnnotationLayer = "CE-SECTION-DETAIL-ANNO";
00026:         private const double Tolerance = 0.000001;
00027: 
00028:         [CommandMethod("CE_TOOLS", "CE_SECTIONDETAILTOOLS", CommandFlags.Modal)]
00029:         public void SectionDetailTools()
00030:         {
00031:             Document document = ActiveDocument();
00032:             if (document == null) return;
00033: 
00034:             var options = new PromptKeywordOptions(
00035:                 "\nDetailed section tools [Create/Refresh/Information/Clear] <Create>: ")
00036:             {
00037:                 AllowNone = true
00038:             };
00039:             options.Keywords.Add("Create");
00040:             options.Keywords.Add("Refresh");
00041:             options.Keywords.Add("Information");
00042:             options.Keywords.Add("Clear");
00043:             PromptResult result = document.Editor.GetKeywords(options);
00044:             if (result.Status == PromptStatus.Cancel) return;
00045: 
00046:             string choice = result.Status == PromptStatus.OK
00047:                 ? result.StringResult
00048:                 : "Create";
00049:             string command;
00050:             if (Equal(choice, "Refresh")) command = "CE_SECTIONDETAILREFRESH ";
00051:             else if (Equal(choice, "Information")) command = "CE_SECTIONDETAILINFO ";
00052:             else if (Equal(choice, "Clear")) command = "CE_SECTIONDETAILCLEAR ";
00053:             else command = "CE_SECTIONDETAILCREATE ";
00054:             document.SendStringToExecute(command, true, false, true);
00055:         }
00056: 
00057:         [CommandMethod(
00058:             "CE_TOOLS",
00059:             "CE_SECTIONDETAILCREATE",
00060:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00061:         public void CreateSectionDetail()
00062:         {
00063:             Document document = ActiveDocument();
00064:             if (document == null) return;
00065:             Editor editor = document.Editor;
00066: 
00067:             PromptSelectionResult selection = PromptSources(editor);
00068:             if (selection.Status != PromptStatus.OK) return;
00069: 
00070:             var sourceIds = new List<ObjectId>();
00071:             int rejected;
00072:             DetailedSectionSnapshot snapshot = BuildSnapshot(
00073:                 document.Database,
00074:                 selection.Value.GetObjectIds(),
00075:                 sourceIds,
00076:                 out rejected);
00077:             if (snapshot == null || sourceIds.Count == 0)
00078:             {
00079:                 editor.WriteMessage(
00080:                     "\nCE_SECTIONDETAILCREATE stopped. No supported editable section geometry was selected.");
00081:                 return;
00082:             }
00083: 
00084:             SectionDetailDiscipline discipline;
00085:             if (!PromptDiscipline(editor, out discipline)) return;
00086: 
00087:             double defaultHeight = document.Database.Textsize > Tolerance
00088:                 ? document.Database.Textsize
00089:                 : 2.5;
00090:             double textHeight;
00091:             if (!PromptPositiveDouble(
00092:                     editor,
00093:                     "Annotation text height",
00094:                     defaultHeight,
00095:                     out textHeight))
00096:                 return;
00097: 
```

### Lines 950-1032
```csharp
00950:             {
00951:                 objectId = database.GetObjectId(
00952:                     false,
00953:                     new Handle(value),
00954:                     0);
00955:                 return !objectId.IsNull && !objectId.IsErased;
00956:             }
00957:             catch
00958:             {
00959:                 return false;
00960:             }
00961:         }
00962: 
00963:         private static bool PromptDiscipline(
00964:             Editor editor,
00965:             out SectionDetailDiscipline discipline)
00966:         {
00967:             discipline = SectionDetailDiscipline.Road;
00968:             var options = new PromptKeywordOptions(
00969:                 "\nDetailed section discipline [Road/Parking/Stormwater/Sewer/Water] <Road>: ")
00970:             {
00971:                 AllowNone = true
00972:             };
00973:             options.Keywords.Add("Road");
00974:             options.Keywords.Add("Parking");
00975:             options.Keywords.Add("Stormwater");
00976:             options.Keywords.Add("Sewer");
00977:             options.Keywords.Add("Water");
00978:             PromptResult result = editor.GetKeywords(options);
00979:             if (result.Status == PromptStatus.Cancel) return false;
00980:             string value = result.Status == PromptStatus.OK
00981:                 ? result.StringResult
00982:                 : "Road";
00983:             return Enum.TryParse(value, true, out discipline);
00984:         }
00985: 
00986:         private static bool PromptPositiveDouble(
00987:             Editor editor,
00988:             string label,
00989:             double defaultValue,
00990:             out double value)
00991:         {
00992:             var options = new PromptDoubleOptions(
00993:                 "\n" + label + " <" +
00994:                 defaultValue.ToString("0.###", CultureInfo.InvariantCulture) +
00995:                 ">: ")
00996:             {
00997:                 AllowNone = true,
00998:                 AllowNegative = false,
00999:                 AllowZero = false,
01000:                 DefaultValue = defaultValue,
01001:                 UseDefaultValue = true
01002:             };
01003:             PromptDoubleResult result = editor.GetDouble(options);
01004:             if (result.Status == PromptStatus.Cancel)
01005:             {
01006:                 value = defaultValue;
01007:                 return false;
01008:             }
01009:             value = result.Status == PromptStatus.OK
01010:                 ? result.Value
01011:                 : defaultValue;
01012:             return result.Status == PromptStatus.OK ||
01013:                    result.Status == PromptStatus.None;
01014:         }
01015: 
01016:         private static string DisciplineTitle(
01017:             SectionDetailDiscipline discipline)
01018:         {
01019:             switch (discipline)
01020:             {
01021:                 case SectionDetailDiscipline.Parking:
01022:                     return "PARKING / DRIVEWAY TYPICAL SECTION";
01023:                 case SectionDetailDiscipline.Stormwater:
01024:                     return "STORMWATER TRENCH / PIPE TYPICAL SECTION";
01025:                 case SectionDetailDiscipline.Sewer:
01026:                     return "SEWER TRENCH / PIPE TYPICAL SECTION";
01027:                 case SectionDetailDiscipline.Water:
01028:                     return "WATER TRENCH / PIPE TYPICAL SECTION";
01029:                 default:
01030:                     return "ROAD TYPICAL SECTION";
01031:             }
01032:         }
```

## DynamicCrossSectionCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 293-372
```csharp
00293:             }
00294:         }
00295: 
00296:         [CommandMethod(
00297:             "CE_TOOLS",
00298:             "CE_XSDETACH",
00299:             CommandFlags.Modal | CommandFlags.Redraw)]
00300:         public void Detach()
00301:         {
00302:             Document document = ActiveDocument();
00303:             if (document == null) return;
00304: 
00305:             ObjectId sourceId;
00306:             if (!PromptForLinkedSectionSource(
00307:                 document,
00308:                 "\nSelect a linked section line or generated section object to detach: ",
00309:                 out sourceId)) return;
00310: 
00311:             var options = new PromptKeywordOptions(
00312:                 "\nGenerated section geometry [Keep/Delete] <Keep>: ")
00313:             {
00314:                 AllowNone = true
00315:             };
00316:             options.Keywords.Add("Keep");
00317:             options.Keywords.Add("Delete");
00318:             PromptResult result = document.Editor.GetKeywords(options);
00319:             if (result.Status == PromptStatus.Cancel) return;
00320:             bool deleteGenerated = result.Status == PromptStatus.OK &&
00321:                 Equal(result.StringResult, "Delete");
00322: 
00323:             if (!Confirm(
00324:                 document.Editor,
00325:                 deleteGenerated
00326:                     ? "Detach the link and delete generated geometry"
00327:                     : "Detach the link and keep generated geometry")) return;
00328: 
00329:             try
00330:             {
00331:                 DynamicSectionUpdateManager.BeginInternalUpdate();
00332:                 using (Transaction transaction =
00333:                     document.Database.TransactionManager.StartTransaction())
00334:                 {
00335:                     Entity source = transaction.GetObject(
00336:                         sourceId,
00337:                         OpenMode.ForWrite,
00338:                         false) as Entity;
00339:                     SectionLink link = ReadLink(source, transaction);
00340: 
00341:                     if (deleteGenerated)
00342:                     {
00343:                         foreach (string handle in link.GeneratedHandles)
00344:                         {
00345:                             ObjectId id;
00346:                             if (!TryResolveHandle(document.Database, handle, out id))
00347:                                 continue;
00348:                             Entity generated = transaction.GetObject(
00349:                                 id,
00350:                                 OpenMode.ForWrite,
00351:                                 false) as Entity;
00352:                             if (generated != null && !generated.IsErased)
00353:                                 generated.Erase();
00354:                         }
00355:                     }
00356:                     else
00357:                     {
00358:                         foreach (string handle in link.GeneratedHandles)
00359:                         {
00360:                             ObjectId id;
00361:                             if (!TryResolveHandle(document.Database, handle, out id))
00362:                                 continue;
00363:                             Entity generated = transaction.GetObject(
00364:                                 id,
00365:                                 OpenMode.ForWrite,
00366:                                 false) as Entity;
00367:                             if (generated != null)
00368:                                 RemoveRecord(generated, transaction, GeneratedRecordName);
00369:                         }
00370:                     }
00371: 
00372:                     RemoveRecord(source, transaction, LinkRecordName);
```

### Lines 1797-1876
```csharp
01797:             double defaultValue,
01798:             out double value)
01799:         {
01800:             var options = new PromptDoubleOptions(message)
01801:             {
01802:                 AllowNone = true,
01803:                 AllowNegative = false,
01804:                 AllowZero = false,
01805:                 DefaultValue = defaultValue,
01806:                 UseDefaultValue = true
01807:             };
01808:             PromptDoubleResult result = editor.GetDouble(options);
01809:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
01810:             return result.Status == PromptStatus.OK && IsFinitePositive(value);
01811:         }
01812: 
01813:         private static bool Confirm(Editor editor, string message)
01814:         {
01815:             var options = new PromptKeywordOptions(
01816:                 "\n" + message + "? [Yes/No] <No>: ")
01817:             {
01818:                 AllowNone = true
01819:             };
01820:             options.Keywords.Add("Yes");
01821:             options.Keywords.Add("No");
01822:             PromptResult result = editor.GetKeywords(options);
01823:             return result.Status == PromptStatus.OK &&
01824:                 Equal(result.StringResult, "Yes");
01825:         }
01826: 
01827:         private static bool TryParseInvariant(string text, out double value)
01828:         {
01829:             return double.TryParse(
01830:                 text,
01831:                 NumberStyles.Float,
01832:                 CultureInfo.InvariantCulture,
01833:                 out value);
01834:         }
01835: 
01836:         private static double Distance2d(Point3d first, Point3d second)
01837:         {
01838:             return new Point2d(first.X, first.Y).GetDistanceTo(
01839:                 new Point2d(second.X, second.Y));
01840:         }
01841: 
01842:         private static double ResolveTextHeight(Database database)
01843:         {
01844:             double height = database == null ? 2.0 : database.Textsize;
01845:             if (Math.Abs(height - 1.8) < 0.05) return 1.8;
01846:             if (Math.Abs(height - 5.0) < 0.05) return 5.0;
01847:             return 2.0;
01848:         }
01849: 
01850:         private static double ResolveDatumInterval(double range)
01851:         {
01852:             if (range <= 5.0) return 0.5;
01853:             if (range <= 20.0) return 1.0;
01854:             if (range <= 100.0) return 5.0;
01855:             if (range <= 500.0) return 10.0;
01856:             return 50.0;
01857:         }
01858: 
01859:         private static bool ContainsAny(string source, params string[] values)
01860:         {
01861:             if (string.IsNullOrWhiteSpace(source)) return false;
01862:             foreach (string value in values)
01863:             {
01864:                 if (source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
01865:                     return true;
01866:             }
01867:             return false;
01868:         }
01869: 
01870:         private static string FriendlyTypeName(string value)
01871:         {
01872:             if (string.IsNullOrWhiteSpace(value)) return "Design element";
01873:             var characters = new List<char>();
01874:             for (int index = 0; index < value.Length; index++)
01875:             {
01876:                 char character = value[index];
```

## DynamicIntersectionCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 24-107
```csharp
00024:     /// </summary>
00025:     public sealed class DynamicIntersectionCommands
00026:     {
00027:         internal const string LinkRecordName = "CE_DYNAMIC_INTERSECTION_SET";
00028:         internal const string GeneratedRecordName = "CE_DYNAMIC_INTERSECTION_GENERATED";
00029:         private const string SettingsDictionary = "CE_TOOLS";
00030:         private const string SettingsRecord = "DYNAMIC_INTERSECTION_SETTINGS";
00031:         private const string SchemaVersion = "1";
00032:         private const string DefaultLayer = "CE-DYNAMIC-INTERSECTIONS";
00033:         private const double GeometryTolerance = 1e-9;
00034: 
00035:         [CommandMethod("CE_INTTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
00036:         public void IntersectionTools()
00037:         {
00038:             Document document = ActiveDocument();
00039:             if (document == null)
00040:                 return;
00041: 
00042:             var options = new PromptKeywordOptions(
00043:                 "\nDynamic intersection tools [Create/Refresh/Information/Detach/Settings/Monitor] <Create>: ")
00044:             {
00045:                 AllowNone = true
00046:             };
00047:             foreach (string keyword in new[]
00048:             {
00049:                 "Create", "Refresh", "Information", "Detach", "Settings", "Monitor"
00050:             })
00051:                 options.Keywords.Add(keyword);
00052: 
00053:             PromptResult result = document.Editor.GetKeywords(options);
00054:             if (result.Status == PromptStatus.Cancel)
00055:                 return;
00056: 
00057:             string choice = result.Status == PromptStatus.OK
00058:                 ? result.StringResult
00059:                 : "Create";
00060:             if (choice.Equals("Refresh", StringComparison.OrdinalIgnoreCase))
00061:                 Refresh();
00062:             else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase))
00063:                 Information();
00064:             else if (choice.Equals("Detach", StringComparison.OrdinalIgnoreCase))
00065:                 Detach();
00066:             else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
00067:                 Settings();
00068:             else if (choice.Equals("Monitor", StringComparison.OrdinalIgnoreCase))
00069:                 Monitor();
00070:             else
00071:                 Create();
00072:         }
00073: 
00074:         [CommandMethod("CE_INTSETTINGS", CommandFlags.Modal)]
00075:         public void Settings()
00076:         {
00077:             Document document = ActiveDocument();
00078:             if (document == null)
00079:                 return;
00080: 
00081:             Editor editor = document.Editor;
00082:             IntersectionSettings settings = IntersectionSettings.Read(document.Database);
00083:             if (!PromptText(editor, "Output layer", settings.Layer, out settings.Layer))
00084:                 return;
00085:             if (!PromptPositiveDouble(editor, "Marker radius", settings.MarkerRadius, out settings.MarkerRadius))
00086:                 return;
00087:             if (!PromptPositiveDouble(editor, "Label height", settings.LabelHeight, out settings.LabelHeight))
00088:                 return;
00089:             if (!PromptPositiveDouble(editor, "XY intersection tolerance", settings.XyTolerance, out settings.XyTolerance))
00090:                 return;
00091:             if (!PromptNonNegativeDouble(editor, "Elevation warning difference", settings.ElevationWarning, out settings.ElevationWarning))
00092:                 return;
00093:             if (!PromptPositiveDouble(editor, "Maximum curve sampling segment", settings.CurveSampleLength, out settings.CurveSampleLength))
00094:                 return;
00095:             if (!PromptPositiveInteger(editor, "Maximum generated intersections", settings.MaximumIntersections, out settings.MaximumIntersections))
00096:                 return;
00097:             if (!PromptText(editor, "Corridor feature-code filter (blank = all)", settings.CorridorCodeFilter, out settings.CorridorCodeFilter))
00098:                 return;
00099: 
00100:             settings.Write(document.Database);
00101:             editor.WriteMessage("\nCE_INTSETTINGS saved in the current DWG.");
00102:         }
00103: 
00104:         [CommandMethod("CE_INTCREATE", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
00105:         public void Create()
00106:         {
00107:             Document document = ActiveDocument();
```

### Lines 269-348
```csharp
00269:         [CommandMethod("CE_INTDETACH", CommandFlags.Modal | CommandFlags.Redraw)]
00270:         public void Detach()
00271:         {
00272:             Document document = ActiveDocument();
00273:             if (document == null)
00274:                 return;
00275: 
00276:             ObjectId anchorId;
00277:             if (!PromptLinkedAnchor(document.Editor, document.Database, out anchorId))
00278:                 return;
00279: 
00280:             IntersectionLink link;
00281:             using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00282:             {
00283:                 Entity anchor = transaction.GetObject(anchorId, OpenMode.ForRead, false) as Entity;
00284:                 link = ReadLink(anchor, transaction);
00285:             }
00286: 
00287:             var options = new PromptKeywordOptions(
00288:                 "\nDetach generated objects [Keep/Delete] <Keep>: ")
00289:             {
00290:                 AllowNone = true
00291:             };
00292:             options.Keywords.Add("Keep");
00293:             options.Keywords.Add("Delete");
00294:             PromptResult result = document.Editor.GetKeywords(options);
00295:             if (result.Status == PromptStatus.Cancel)
00296:                 return;
00297:             bool deleteGenerated = result.Status == PromptStatus.OK &&
00298:                 result.StringResult.Equals("Delete", StringComparison.OrdinalIgnoreCase);
00299: 
00300:             try
00301:             {
00302:                 DynamicIntersectionUpdateManager.BeginInternalUpdate();
00303:                 using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00304:                 {
00305:                     foreach (string handle in link.GeneratedHandles)
00306:                     {
00307:                         ObjectId id;
00308:                         if (!TryResolveHandle(document.Database, handle, out id))
00309:                             continue;
00310:                         Entity generated = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
00311:                         if (generated == null)
00312:                             continue;
00313:                         if (deleteGenerated)
00314:                             generated.Erase();
00315:                         else
00316:                             RemoveRecord(generated, transaction, GeneratedRecordName);
00317:                     }
00318:                     Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
00319:                     if (anchor != null)
00320:                         anchor.Erase();
00321:                     transaction.Commit();
00322:                 }
00323:                 DynamicIntersectionUpdateManager.UnregisterLinkedSet(document, anchorId);
00324:                 document.Editor.WriteMessage(deleteGenerated
00325:                     ? "\nCE_INTDETACH complete. Link anchor and generated intersection objects were removed."
00326:                     : "\nCE_INTDETACH complete. Link anchor was removed and generated geometry was kept as ordinary drawing objects.");
00327:             }
00328:             catch (System.Exception exception)
00329:             {
00330:                 document.Editor.WriteMessage("\nCE_INTDETACH failed. " + exception.Message);
00331:             }
00332:             finally
00333:             {
00334:                 DynamicIntersectionUpdateManager.EndInternalUpdate();
00335:             }
00336:         }
00337: 
00338:         [CommandMethod("CE_INTMONITOR", CommandFlags.Modal)]
00339:         public void Monitor()
00340:         {
00341:             Document document = ActiveDocument();
00342:             if (document == null)
00343:                 return;
00344:             document.Editor.WriteMessage(
00345:                 "\nCE Dynamic Intersection Monitor" +
00346:                 "\n  Initialised: " + DynamicIntersectionUpdateManager.IsInitialized +
00347:                 "\n  Linked sets in current space: " + FindLinkedAnchors(document.Database).Count +
00348:                 "\n  Pending refresh: " + DynamicIntersectionUpdateManager.HasPendingRefresh(document) +
```

### Lines 1561-1716
```csharp
01561:         }
01562: 
01563:         private static string Escape(string value)
01564:         {
01565:             return (value ?? string.Empty).Replace("%", "%25").Replace("|", "%7C");
01566:         }
01567: 
01568:         private static string Unescape(string value)
01569:         {
01570:             return (value ?? string.Empty).Replace("%7C", "|").Replace("%25", "%");
01571:         }
01572: 
01573:         private static bool PromptText(
01574:             Editor editor,
01575:             string label,
01576:             string current,
01577:             out string value)
01578:         {
01579:             var options = new PromptStringOptions(
01580:                 "\n" + label + " <" + (current ?? string.Empty) + ">: ")
01581:             {
01582:                 AllowSpaces = true
01583:             };
01584:             PromptResult result = editor.GetString(options);
01585:             if (result.Status == PromptStatus.Cancel)
01586:             {
01587:                 value = current;
01588:                 return false;
01589:             }
01590:             value = result.Status == PromptStatus.None
01591:                 ? current
01592:                 : result.StringResult.Trim();
01593:             return true;
01594:         }
01595: 
01596:         private static bool PromptPositiveDouble(
01597:             Editor editor,
01598:             string label,
01599:             double current,
01600:             out double value)
01601:         {
01602:             var options = new PromptDoubleOptions(
01603:                 "\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
01604:             {
01605:                 AllowNegative = false,
01606:                 AllowZero = false,
01607:                 UseDefaultValue = true,
01608:                 DefaultValue = current
01609:             };
01610:             PromptDoubleResult result = editor.GetDouble(options);
01611:             value = result.Status == PromptStatus.OK ? result.Value : current;
01612:             return result.Status == PromptStatus.OK;
01613:         }
01614: 
01615:         private static bool PromptNonNegativeDouble(
01616:             Editor editor,
01617:             string label,
01618:             double current,
01619:             out double value)
01620:         {
01621:             var options = new PromptDoubleOptions(
01622:                 "\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
01623:             {
01624:                 AllowNegative = false,
01625:                 AllowZero = true,
01626:                 UseDefaultValue = true,
01627:                 DefaultValue = current
01628:             };
01629:             PromptDoubleResult result = editor.GetDouble(options);
01630:             value = result.Status == PromptStatus.OK ? result.Value : current;
01631:             return result.Status == PromptStatus.OK;
01632:         }
01633: 
01634:         private static bool PromptPositiveInteger(
01635:             Editor editor,
01636:             string label,
01637:             int current,
01638:             out int value)
01639:         {
01640:             var options = new PromptIntegerOptions(
01641:                 "\n" + label + " <" + current.ToString(CultureInfo.InvariantCulture) + ">: ")
01642:             {
01643:                 AllowNegative = false,
01644:                 AllowZero = false,
01645:                 UseDefaultValue = true,
01646:                 DefaultValue = current
01647:             };
01648:             PromptIntegerResult result = editor.GetInteger(options);
01649:             value = result.Status == PromptStatus.OK ? result.Value : current;
01650:             return result.Status == PromptStatus.OK;
01651:         }
01652: 
01653:         private static bool Confirm(Editor editor, string message)
01654:         {
01655:             var options = new PromptKeywordOptions(
01656:                 "\n" + message + "? [Yes/No] <No>: ")
01657:             {
01658:                 AllowNone = true
01659:             };
01660:             options.Keywords.Add("Yes");
01661:             options.Keywords.Add("No");
01662:             PromptResult result = editor.GetKeywords(options);
01663:             return result.Status == PromptStatus.OK &&
01664:                    result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
01665:         }
01666: 
01667:         private static Document ActiveDocument()
01668:         {
01669:             return AcApplication.DocumentManager.MdiActiveDocument;
01670:         }
01671: 
01672:         private sealed class SourceRecord
01673:         {
01674:             public SourceRecord(
01675:                 ObjectId sourceId,
01676:                 string sourceHandle,
01677:                 string sourceName,
01678:                 string sourceType,
01679:                 IReadOnlyList<DesignPath> paths)
01680:             {
01681:                 SourceId = sourceId;
01682:                 SourceHandle = sourceHandle;
01683:                 SourceName = sourceName;
01684:                 SourceType = sourceType;
01685:                 Paths = paths;
01686:             }
01687:             public ObjectId SourceId { get; }
01688:             public string SourceHandle { get; }
01689:             public string SourceName { get; }
01690:             public string SourceType { get; }
01691:             public IReadOnlyList<DesignPath> Paths { get; }
01692:         }
01693: 
01694:         private sealed class DesignPath
01695:         {
01696:             public DesignPath(string name, IReadOnlyList<Point3d> points)
01697:             {
01698:                 Name = name ?? string.Empty;
01699:                 Points = points;
01700:             }
01701:             public string Name { get; }
01702:             public IReadOnlyList<Point3d> Points { get; }
01703:         }
01704: 
01705:         private sealed class ExtractionResult
01706:         {
01707:             public ExtractionResult(List<IntersectionHit> intersections, int segmentPairsTested)
01708:             {
01709:                 Intersections = intersections;
01710:                 SegmentPairsTested = segmentPairsTested;
01711:             }
01712:             public List<IntersectionHit> Intersections { get; }
01713:             public int SegmentPairsTested { get; }
01714:         }
01715: 
01716:         private sealed class IntersectionHit
```

## DynamicTypicalDetailCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 32-116
```csharp
00032:         private const string SettingsRecordName = "DYNAMIC_TYPICAL_DETAIL_SETTINGS";
00033:         private const string SchemaVersion = "2";
00034:         private const string DefaultDetailLayer = "CE-DYNAMIC-DETAIL";
00035:         private const string DefaultBoqLayer = "CE-DYNAMIC-DETAIL-BOQ";
00036:         private const double GeometryTolerance = 1e-9;
00037: 
00038:         private static readonly string[] SupportedTypes =
00039:         {
00040:             "TrenchDrain", "PipeTrench", "ValveChamber", "Kerb", "Headwall"
00041:         };
00042: 
00043:         [CommandMethod("CE_DETAILPARAMTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
00044:         public void DynamicDetailTools()
00045:         {
00046:             Document document = ActiveDocument();
00047:             if (document == null)
00048:                 return;
00049: 
00050:             var options = new PromptKeywordOptions(
00051:                 "\nDynamic typical-detail tools [Create/Edit/Refresh/BOQ/Export/Review/Information/Detach/Clear/Settings] <Create>: ")
00052:             {
00053:                 AllowNone = true
00054:             };
00055:             foreach (string keyword in new[]
00056:             {
00057:                 "Create", "Edit", "Refresh", "BOQ", "Export", "Review",
00058:                 "Information", "Detach", "Clear", "Settings"
00059:             })
00060:                 options.Keywords.Add(keyword);
00061: 
00062:             PromptResult result = document.Editor.GetKeywords(options);
00063:             if (result.Status == PromptStatus.Cancel)
00064:                 return;
00065: 
00066:             string choice = result.Status == PromptStatus.OK ? result.StringResult : "Create";
00067:             if (choice.Equals("Edit", StringComparison.OrdinalIgnoreCase)) EditParameters();
00068:             else if (choice.Equals("Refresh", StringComparison.OrdinalIgnoreCase)) Refresh();
00069:             else if (choice.Equals("BOQ", StringComparison.OrdinalIgnoreCase)) RefreshBoq();
00070:             else if (choice.Equals("Export", StringComparison.OrdinalIgnoreCase)) ExportBoq();
00071:             else if (choice.Equals("Review", StringComparison.OrdinalIgnoreCase)) RecordReviewStatus();
00072:             else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase)) Information();
00073:             else if (choice.Equals("Detach", StringComparison.OrdinalIgnoreCase)) Detach();
00074:             else if (choice.Equals("Clear", StringComparison.OrdinalIgnoreCase)) Clear();
00075:             else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase)) ConfigureSettings();
00076:             else Create();
00077:         }
00078: 
00079:         [CommandMethod("CE_DETAILPARAMSETTINGS", CommandFlags.Modal)]
00080:         public void ConfigureSettings()
00081:         {
00082:             Document document = ActiveDocument();
00083:             if (document == null)
00084:                 return;
00085: 
00086:             Editor editor = document.Editor;
00087:             DynamicDetailSettings settings = DynamicDetailSettings.Read(document.Database);
00088:             if (!PromptPositiveDouble(editor, "Drawing units per metre (1000 for mm drawings, 1 for metre drawings)", settings.DrawingUnitsPerMetre, out settings.DrawingUnitsPerMetre)) return;
00089:             if (!PromptPositiveDouble(editor, "Text height in drawing units", settings.TextHeight, out settings.TextHeight)) return;
00090:             if (!PromptPositiveDouble(editor, "Dimension offset in drawing units", settings.DimensionOffset, out settings.DimensionOffset)) return;
00091:             if (!PromptPositiveDouble(editor, "Schedule offset in drawing units", settings.ScheduleOffset, out settings.ScheduleOffset)) return;
00092:             if (!PromptText(editor, "Generated detail layer", settings.DetailLayer, out settings.DetailLayer)) return;
00093:             if (!PromptText(editor, "Generated BOQ layer", settings.BoqLayer, out settings.BoqLayer)) return;
00094: 
00095:             settings.Write(document.Database);
00096:             editor.WriteMessage("\nCE_DETAILPARAMSETTINGS saved. Source detail files will remain read-only during review.");
00097:         }
00098: 
00099:         [CommandMethod("CE_DETAILPARAMCREATE", CommandFlags.Modal | CommandFlags.Redraw)]
00100:         public void Create()
00101:         {
00102:             Document document = ActiveDocument();
00103:             if (document == null)
00104:                 return;
00105: 
00106:             DetailParameters parameters;
00107:             if (!PromptNewParameters(document.Editor, out parameters))
00108:                 return;
00109: 
00110:             string sourcePath = PromptOptionalSourceTemplate(document.Editor);
00111:             string sourceHash = ComputeSha256(sourcePath);
00112:             string sourceModified = ReadSourceModifiedUtc(sourcePath);
00113: 
00114:             PromptPointResult insertion = document.Editor.GetPoint("\nPick the insertion point for the generated dynamic detail: ");
00115:             if (insertion.Status != PromptStatus.OK)
00116:                 return;
```

### Lines 270-351
```csharp
00270:             catch (System.Exception exception)
00271:             {
00272:                 document.Editor.WriteMessage("\nCE_DETAILPARAMBOQEXPORT failed. " + exception.Message);
00273:             }
00274:         }
00275: 
00276:         [CommandMethod("CE_DETAILPARAMREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
00277:         public void RecordReviewStatus()
00278:         {
00279:             Document document = ActiveDocument();
00280:             if (document == null)
00281:                 return;
00282: 
00283:             ObjectId anchorId;
00284:             DynamicDetailLink link;
00285:             if (!PromptLinkedDetail(document, out anchorId, out link))
00286:                 return;
00287: 
00288:             var options = new PromptKeywordOptions(
00289:                 "\nRecord detail review status [Draft/ForReview/Reviewed/ApprovedRecord] <" + StatusKeyword(link.Parameters.ReviewStatus) + ">: ")
00290:             {
00291:                 AllowNone = true
00292:             };
00293:             options.Keywords.Add("Draft");
00294:             options.Keywords.Add("ForReview");
00295:             options.Keywords.Add("Reviewed");
00296:             options.Keywords.Add("ApprovedRecord");
00297:             PromptResult result = document.Editor.GetKeywords(options);
00298:             if (result.Status == PromptStatus.Cancel)
00299:                 return;
00300: 
00301:             string keyword = result.Status == PromptStatus.OK ? result.StringResult : StatusKeyword(link.Parameters.ReviewStatus);
00302:             string status = keyword.Equals("ApprovedRecord", StringComparison.OrdinalIgnoreCase)
00303:                 ? "Approved (recorded)"
00304:                 : keyword.Equals("ForReview", StringComparison.OrdinalIgnoreCase) ? "For Review" : keyword;
00305: 
00306:             string reviewer = string.Empty;
00307:             if (!status.Equals("Draft", StringComparison.OrdinalIgnoreCase))
00308:             {
00309:                 if (!PromptText(document.Editor, "Reviewer/approver name or reference", link.Parameters.Reviewer, out reviewer) || string.IsNullOrWhiteSpace(reviewer))
00310:                 {
00311:                     document.Editor.WriteMessage("\nA reviewer/approver name or reference is required for non-Draft status.");
00312:                     return;
00313:                 }
00314:             }
00315: 
00316:             if (status.StartsWith("Approved", StringComparison.OrdinalIgnoreCase))
00317:             {
00318:                 document.Editor.WriteMessage("\nIMPORTANT: CE Tools records the entered status only. It cannot verify professional registration, delegated authority or engineering approval.");
00319:                 if (!Confirm(document.Editor, "Record this user-supplied approval status after external authority has been verified"))
00320:                     return;
00321:             }
00322: 
00323:             DetailParameters parameters = link.Parameters.Clone();
00324:             parameters.ReviewStatus = status;
00325:             parameters.Reviewer = reviewer;
00326:             parameters.ReviewedAtUtc = status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
00327:                 ? string.Empty
00328:                 : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
00329:             Regenerate(document, anchorId, link.WithParameters(parameters), true, "CE_DETAILPARAMREVIEW");
00330:         }
00331: 
00332:         [CommandMethod("CE_DETAILPARAMINFO", CommandFlags.Modal)]
00333:         public void Information()
00334:         {
00335:             Document document = ActiveDocument();
00336:             if (document == null)
00337:                 return;
00338: 
00339:             ObjectId anchorId;
00340:             DynamicDetailLink link;
00341:             if (!PromptLinkedDetail(document, out anchorId, out link))
00342:                 return;
00343: 
00344:             bool sourceExists = !string.IsNullOrWhiteSpace(link.SourcePath) && File.Exists(link.SourcePath);
00345:             string currentHash = ComputeSha256(link.SourcePath);
00346:             string sourceState = string.IsNullOrWhiteSpace(link.SourcePath)
00347:                 ? "No external source selected"
00348:                 : !sourceExists ? "Missing"
00349:                 : currentHash.Equals(link.SourceHash, StringComparison.OrdinalIgnoreCase) ? "Live / hash matches" : "Live / hash changed";
00350:             int liveGenerated = link.GeneratedHandles.Count(handle =>
00351:             {
```

### Lines 389-518
```csharp
00389:                 note,
00390:                 new List<string> { "Property", "Value" },
00391:                 rows,
00392:                 "CE Dynamic Typical Detail - " + link.DetailId);
00393:         }
00394: 
00395:         [CommandMethod("CE_DETAILPARAMDETACH", CommandFlags.Modal | CommandFlags.Redraw)]
00396:         public void Detach()
00397:         {
00398:             Document document = ActiveDocument();
00399:             if (document == null)
00400:                 return;
00401: 
00402:             ObjectId anchorId;
00403:             DynamicDetailLink link;
00404:             if (!PromptLinkedDetail(document, out anchorId, out link))
00405:                 return;
00406: 
00407:             var options = new PromptKeywordOptions("\nDetach generated detail [Keep/Delete] <Keep>: ") { AllowNone = true };
00408:             options.Keywords.Add("Keep");
00409:             options.Keywords.Add("Delete");
00410:             PromptResult result = document.Editor.GetKeywords(options);
00411:             if (result.Status == PromptStatus.Cancel)
00412:                 return;
00413:             bool deleteGenerated = result.Status == PromptStatus.OK && result.StringResult.Equals("Delete", StringComparison.OrdinalIgnoreCase);
00414: 
00415:             if (!Confirm(document.Editor, deleteGenerated
00416:                 ? "Delete the linked generated variant, schedules and anchor"
00417:                 : "Detach the link and keep generated geometry/schedules as ordinary drawing objects"))
00418:                 return;
00419: 
00420:             try
00421:             {
00422:                 using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00423:                 {
00424:                     foreach (string handle in link.GeneratedHandles)
00425:                     {
00426:                         ObjectId id;
00427:                         if (!TryResolveHandle(document.Database, handle, out id))
00428:                             continue;
00429:                         Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
00430:                         if (entity == null)
00431:                             continue;
00432:                         if (deleteGenerated) entity.Erase();
00433:                         else
00434:                         {
00435:                             RemoveExtensionRecord(entity, transaction, GeneratedRecordName);
00436:                             RemoveExtensionRecord(entity, transaction, BoqLinkRecordName);
00437:                         }
00438:                     }
00439:                     Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
00440:                     if (anchor != null)
00441:                         anchor.Erase();
00442:                     transaction.Commit();
00443:                 }
00444:                 document.Editor.WriteMessage(deleteGenerated
00445:                     ? "\nCE_DETAILPARAMDETACH complete. Generated variant, schedules and anchor were deleted. The source template was unchanged."
00446:                     : "\nCE_DETAILPARAMDETACH complete. The anchor was removed; generated objects were kept as ordinary drawing content.");
00447:             }
00448:             catch (System.Exception exception)
00449:             {
00450:                 document.Editor.WriteMessage("\nCE_DETAILPARAMDETACH failed. " + exception.Message);
00451:             }
00452:         }
00453: 
00454:         [CommandMethod("CE_DETAILPARAMCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
00455:         public void Clear()
00456:         {
00457:             Document document = ActiveDocument();
00458:             if (document == null)
00459:                 return;
00460: 
00461:             var options = new PromptKeywordOptions("\nClear dynamic typical details [Selected/AllCurrentSpace] <Selected>: ") { AllowNone = true };
00462:             options.Keywords.Add("Selected");
00463:             options.Keywords.Add("AllCurrentSpace");
00464:             PromptResult result = document.Editor.GetKeywords(options);
00465:             if (result.Status == PromptStatus.Cancel)
00466:                 return;
00467: 
00468:             List<ObjectId> anchors = result.Status == PromptStatus.OK && result.StringResult.Equals("AllCurrentSpace", StringComparison.OrdinalIgnoreCase)
00469:                 ? FindAnchorsInCurrentSpace(document.Database)
00470:                 : PromptAnchorSelection(document);
00471:             if (anchors.Count == 0)
00472:             {
00473:                 document.Editor.WriteMessage("\nCE_DETAILPARAMCLEAR: no linked dynamic-detail anchors were found.");
00474:                 return;
00475:             }
00476: 
00477:             int generatedCount = CountGenerated(document.Database, anchors);
00478:             document.Editor.WriteMessage("\nCE_DETAILPARAMCLEAR preview: anchors={0}; linked generated objects={1}.", anchors.Count, generatedCount);
00479:             if (!Confirm(document.Editor, "Delete these linked dynamic-detail anchors and their CE-generated geometry/schedules"))
00480:                 return;
00481: 
00482:             try
00483:             {
00484:                 int deletedAnchors = 0;
00485:                 int deletedGenerated = 0;
00486:                 using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00487:                 {
00488:                     foreach (ObjectId anchorId in anchors.Distinct())
00489:                     {
00490:                         Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
00491:                         if (anchor == null || !HasExtensionRecord(anchor, transaction, LinkRecordName))
00492:                             continue;
00493:                         DynamicDetailLink link = ReadLink(anchor, transaction);
00494:                         foreach (string handle in link.GeneratedHandles)
00495:                         {
00496:                             ObjectId id;
00497:                             if (!TryResolveHandle(document.Database, handle, out id))
00498:                                 continue;
00499:                             Entity generated = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
00500:                             if (generated != null && HasExtensionRecord(generated, transaction, GeneratedRecordName))
00501:                             {
00502:                                 generated.Erase();
00503:                                 deletedGenerated++;
00504:                             }
00505:                         }
00506:                         anchor.Erase();
00507:                         deletedAnchors++;
00508:                     }
00509:                     transaction.Commit();
00510:                 }
00511:                 document.Editor.WriteMessage(
00512:                     "\nCE_DETAILPARAMCLEAR complete. Anchors deleted={0}; generated objects deleted={1}; source templates modified=0.",
00513:                     deletedAnchors,
00514:                     deletedGenerated);
00515:             }
00516:             catch (System.Exception exception)
00517:             {
00518:                 document.Editor.WriteMessage("\nCE_DETAILPARAMCLEAR failed. No clear transaction was committed. " + exception.Message);
```

## DynamicTypicalDetailStorage.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 262-369
```csharp
00262:             if (layers.Has(name))
00263:             {
00264:                 ObjectId id = layers[name];
00265:                 LayerTableRecord existing = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
00266:                 if (existing != null && existing.IsLocked)
00267:                     throw new InvalidOperationException("Layer '" + name + "' is locked.");
00268:                 return id;
00269:             }
00270:             layers.UpgradeOpen();
00271:             var layer = new LayerTableRecord { Name = name };
00272:             ObjectId layerId = layers.Add(layer);
00273:             transaction.AddNewlyCreatedDBObject(layer, true);
00274:             return layerId;
00275:         }
00276: 
00277:         private static bool PromptNewParameters(Editor editor, out DetailParameters parameters)
00278:         {
00279:             parameters = new DetailParameters();
00280:             var options = new PromptKeywordOptions("\nDynamic detail type [TrenchDrain/PipeTrench/ValveChamber/Kerb/Headwall] <TrenchDrain>: ") { AllowNone = true };
00281:             foreach (string type in SupportedTypes)
00282:                 options.Keywords.Add(type);
00283:             PromptResult result = editor.GetKeywords(options);
00284:             if (result.Status == PromptStatus.Cancel)
00285:                 return false;
00286:             parameters.DetailType = result.Status == PromptStatus.OK ? result.StringResult : "TrenchDrain";
00287:             return PromptEditableParameters(editor, parameters);
00288:         }
00289: 
00290:         private static bool PromptEditableParameters(Editor editor, DetailParameters parameters)
00291:         {
00292:             if (!PromptPositiveDouble(editor, "Overall width in millimetres", parameters.WidthMillimetres, out parameters.WidthMillimetres)) return false;
00293:             if (!PromptPositiveDouble(editor, "Overall depth/height in millimetres", parameters.DepthMillimetres, out parameters.DepthMillimetres)) return false;
00294:             string lengthLabel = parameters.DetailType.Equals("ValveChamber", StringComparison.OrdinalIgnoreCase)
00295:                 ? "Plan length in metres"
00296:                 : parameters.DetailType.Equals("Headwall", StringComparison.OrdinalIgnoreCase)
00297:                     ? "Headwall plan thickness in metres"
00298:                     : "Scheduled detail length in metres";
00299:             if (!PromptPositiveDouble(editor, lengthLabel, parameters.LengthMetres, out parameters.LengthMetres)) return false;
00300:             if (!PromptPositiveDouble(editor, "Wall/base/slab thickness in millimetres", parameters.WallThicknessMillimetres, out parameters.WallThicknessMillimetres)) return false;
00301:             if (!PromptPositiveDouble(editor, "Pipe diameter in millimetres", parameters.PipeDiameterMillimetres, out parameters.PipeDiameterMillimetres)) return false;
00302:             if (!PromptPositiveDouble(editor, "Bedding depth in millimetres", parameters.BeddingDepthMillimetres, out parameters.BeddingDepthMillimetres)) return false;
00303:             if (!PromptText(editor, "Concrete strength/specification", parameters.ConcreteStrength, out parameters.ConcreteStrength)) return false;
00304:             if (!PromptText(editor, "Reinforcement specification", parameters.Reinforcement, out parameters.Reinforcement)) return false;
00305:             if (!PromptText(editor, "Grating/cover type", parameters.GratingType, out parameters.GratingType)) return false;
00306:             parameters.Normalize();
00307:             return true;
00308:         }
00309: 
00310:         private static string PromptOptionalSourceTemplate(Editor editor)
00311:         {
00312:             var options = new PromptKeywordOptions("\nReference an approved source DWG template [Select/None] <None>: ") { AllowNone = true };
00313:             options.Keywords.Add("Select");
00314:             options.Keywords.Add("None");
00315:             PromptResult result = editor.GetKeywords(options);
00316:             if (result.Status != PromptStatus.OK || !result.StringResult.Equals("Select", StringComparison.OrdinalIgnoreCase))
00317:                 return string.Empty;
00318:             var dialog = new OpenFileDialog(
00319:                 "Select approved source DWG template (read-only identity reference)",
00320:                 string.Empty,
00321:                 "dwg",
00322:                 "CE_DETAILPARAMCREATE",
00323:                 OpenFileDialog.OpenFileDialogFlags.DoNotTransferRemoteFiles);
00324:             if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
00325:                 return string.Empty;
00326:             return Path.GetFullPath(dialog.Filename);
00327:         }
00328: 
00329:         private static void WritePreview(Editor editor, DetailParameters parameters, string sourcePath, DynamicDetailSettings settings)
00330:         {
00331:             editor.WriteMessage(
00332:                 "\nCE dynamic-detail preview" +
00333:                 "\n  Type: " + DisplayType(parameters.DetailType) +
00334:                 "\n  Width x depth/height: " + parameters.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " x " + parameters.DepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
00335:                 "\n  Length / plan thickness: " + parameters.LengthMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m" +
00336:                 "\n  Wall/base/slab thickness: " + parameters.WallThicknessMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
00337:                 "\n  Pipe diameter: " + parameters.PipeDiameterMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
00338:                 "\n  Bedding: " + parameters.BeddingDepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
00339:                 "\n  Concrete: " + parameters.ConcreteStrength +
00340:                 "\n  Reinforcement: " + parameters.Reinforcement +
00341:                 "\n  Grating/cover: " + parameters.GratingType +
00342:                 "\n  Source template: " + (string.IsNullOrWhiteSpace(sourcePath) ? "<None / built-in schematic>" : sourcePath) +
00343:                 "\n  Drawing units per metre: " + settings.DrawingUnitsPerMetre.ToString("0.###", CultureInfo.InvariantCulture) +
00344:                 "\n  Source templates remain external/read-only. Generated geometry and quantities require engineer/authority review.");
00345:         }
00346: 
00347:         private static List<ObjectId> PromptAnchorSelection(Document document)
00348:         {
00349:             var ids = new List<ObjectId>();
00350:             var options = new PromptSelectionOptions
00351:             {
00352:                 MessageForAdding = "\nSelect CE dynamic-detail anchors, generated geometry or linked schedules: "
00353:             };
00354:             PromptSelectionResult selection = document.Editor.GetSelection(options);
00355:             if (selection.Status != PromptStatus.OK)
00356:                 return ids;
00357:             using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00358:             {
00359:                 foreach (SelectedObject item in selection.Value)
00360:                 {
00361:                     if (item == null)
00362:                         continue;
00363:                     Entity entity = transaction.GetObject(item.ObjectId, OpenMode.ForRead, false) as Entity;
00364:                     if (entity == null)
00365:                         continue;
00366:                     if (HasExtensionRecord(entity, transaction, LinkRecordName))
00367:                     {
00368:                         ids.Add(item.ObjectId);
00369:                         continue;
```

### Lines 513-631
```csharp
00513:         {
00514:             List<string> values;
00515:             return data.TryGetValue(key, out values) ? new List<string>(values) : new List<string>();
00516:         }
00517: 
00518:         private static double GetDouble(IDictionary<string, List<string>> data, string key, double fallback)
00519:         {
00520:             double value;
00521:             return double.TryParse(Get(data, key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
00522:         }
00523: 
00524:         private static string Encode(string value)
00525:         {
00526:             return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
00527:         }
00528: 
00529:         private static string Decode(string value)
00530:         {
00531:             try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); }
00532:             catch { return string.Empty; }
00533:         }
00534: 
00535:         private static KeyValuePair<string, string> Pair(string key, string value)
00536:         {
00537:             return new KeyValuePair<string, string>(key, value ?? string.Empty);
00538:         }
00539: 
00540:         private static IList<string> Row(string key, string value)
00541:         {
00542:             return new List<string> { key, value ?? string.Empty };
00543:         }
00544: 
00545:         private static bool PromptText(Editor editor, string label, string current, out string value)
00546:         {
00547:             var options = new PromptStringOptions("\n" + label + " <" + (current ?? string.Empty) + ">: ") { AllowSpaces = true };
00548:             PromptResult result = editor.GetString(options);
00549:             if (result.Status == PromptStatus.Cancel)
00550:             {
00551:                 value = current;
00552:                 return false;
00553:             }
00554:             value = result.Status == PromptStatus.None ? current : result.StringResult.Trim();
00555:             return true;
00556:         }
00557: 
00558:         private static bool PromptPositiveDouble(Editor editor, string label, double current, out double value)
00559:         {
00560:             var options = new PromptDoubleOptions("\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
00561:             {
00562:                 AllowNegative = false,
00563:                 AllowZero = false,
00564:                 UseDefaultValue = true,
00565:                 DefaultValue = current
00566:             };
00567:             PromptDoubleResult result = editor.GetDouble(options);
00568:             value = result.Status == PromptStatus.OK ? result.Value : current;
00569:             return result.Status == PromptStatus.OK;
00570:         }
00571: 
00572:         private static bool Confirm(Editor editor, string message)
00573:         {
00574:             var options = new PromptKeywordOptions("\n" + message + "? [Yes/No] <No>: ") { AllowNone = true };
00575:             options.Keywords.Add("Yes");
00576:             options.Keywords.Add("No");
00577:             PromptResult result = editor.GetKeywords(options);
00578:             return result.Status == PromptStatus.OK && result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
00579:         }
00580: 
00581:         private static Document ActiveDocument()
00582:         {
00583:             return AcApplication.DocumentManager.MdiActiveDocument;
00584:         }
00585: 
00586:         private sealed class GeneratedSet
00587:         {
00588:             public GeneratedSet(List<string> handles, string boqTableHandle)
00589:             {
00590:                 Handles = handles;
00591:                 BoqTableHandle = boqTableHandle;
00592:             }
00593:             public List<string> Handles { get; private set; }
00594:             public string BoqTableHandle { get; private set; }
00595:         }
00596: 
00597:         private sealed class QuantityItem
00598:         {
00599:             public QuantityItem(string key, string description, string unit, double quantity, double rate)
00600:             {
00601:                 Key = key;
00602:                 Description = description;
00603:                 Unit = unit;
00604:                 Quantity = quantity;
00605:                 Rate = rate;
00606:             }
00607:             public string Key { get; private set; }
00608:             public string Description { get; private set; }
00609:             public string Unit { get; private set; }
00610:             public double Quantity { get; private set; }
00611:             public double Rate { get; private set; }
00612:             public double Amount { get { return Quantity * Rate; } }
00613:         }
00614: 
00615:         private sealed class DetailParameters
00616:         {
00617:             public string DetailType = "TrenchDrain";
00618:             public double WidthMillimetres = 1000.0;
00619:             public double DepthMillimetres = 1000.0;
00620:             public double LengthMetres = 1.0;
00621:             public double WallThicknessMillimetres = 150.0;
00622:             public double PipeDiameterMillimetres = 300.0;
00623:             public double BeddingDepthMillimetres = 150.0;
00624:             public string ConcreteStrength = "30 MPa";
00625:             public string Reinforcement = "Engineer designed";
00626:             public string GratingType = "Heavy-duty grating / cover";
00627:             public string ReviewStatus = "Draft";
00628:             public string Reviewer = string.Empty;
00629:             public string ReviewedAtUtc = string.Empty;
00630: 
00631:             public DetailParameters Clone()
```

## EngineeringAssetLibraryCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 574-654
```csharp
00574:                 DialogCaption = "Select CE Tools Engineering Asset Catalog"
00575:             };
00576:             if (!string.IsNullOrWhiteSpace(currentPath)) options.InitialDirectory = Path.GetDirectoryName(currentPath);
00577:             PromptFileNameResult result = editor.GetFileNameForOpen(options);
00578:             path = result.Status == PromptStatus.OK ? result.StringResult : string.Empty;
00579:             return result.Status == PromptStatus.OK && (!mustExist || File.Exists(path));
00580:         }
00581: 
00582:         private static bool PromptVisibility(
00583:             Editor editor,
00584:             EngineeringAssetApprovalStatus defaultValue,
00585:             out EngineeringAssetApprovalStatus visibility)
00586:         {
00587:             string defaultKeyword = defaultValue == EngineeringAssetApprovalStatus.Reviewed
00588:                 ? "Reviewed"
00589:                 : defaultValue == EngineeringAssetApprovalStatus.Draft
00590:                     ? "All"
00591:                     : "Approved";
00592:             var options = new PromptKeywordOptions(
00593:                 "\nAsset visibility [Approved/Reviewed/All] <" + defaultKeyword + ">: ")
00594:             {
00595:                 AllowNone = true
00596:             };
00597:             options.Keywords.Add("Approved");
00598:             options.Keywords.Add("Reviewed");
00599:             options.Keywords.Add("All");
00600:             PromptResult result = editor.GetKeywords(options);
00601:             if (result.Status == PromptStatus.Cancel)
00602:             {
00603:                 visibility = defaultValue;
00604:                 return false;
00605:             }
00606:             string value = result.Status == PromptStatus.OK ? result.StringResult : defaultKeyword;
00607:             visibility = Equal(value, "Reviewed")
00608:                 ? EngineeringAssetApprovalStatus.Reviewed
00609:                 : Equal(value, "All")
00610:                     ? EngineeringAssetApprovalStatus.Draft
00611:                     : EngineeringAssetApprovalStatus.Approved;
00612:             return true;
00613:         }
00614: 
00615:         private static string VisibilityLabel(EngineeringAssetApprovalStatus value)
00616:         {
00617:             return value == EngineeringAssetApprovalStatus.Reviewed
00618:                 ? "Approved + Reviewed"
00619:                 : value == EngineeringAssetApprovalStatus.Draft
00620:                     ? "Approved + Reviewed + ForReview + Draft"
00621:                     : "Approved only";
00622:         }
00623: 
00624:         private static bool PromptInsertedAsset(
00625:             Document document,
00626:             out ObjectId objectId,
00627:             out AssetInsertionTag tag)
00628:         {
00629:             objectId = ObjectId.Null;
00630:             tag = null;
00631:             var options = new PromptEntityOptions("\nSelect one CE Tools inserted engineering asset: ");
00632:             options.SetRejectMessage("\nSelect a block reference inserted by CE_ASSETINSERT.");
00633:             options.AddAllowedClass(typeof(BlockReference), true);
00634:             PromptEntityResult result = document.Editor.GetEntity(options);
00635:             if (result.Status != PromptStatus.OK) return false;
00636:             using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00637:             {
00638:                 Entity entity = transaction.GetObject(result.ObjectId, OpenMode.ForRead, false) as Entity;
00639:                 tag = ReadTag(entity);
00640:                 transaction.Commit();
00641:             }
00642:             if (tag == null)
00643:             {
00644:                 document.Editor.WriteMessage("\nThe selected block has no CE engineering asset traceability record.");
00645:                 return false;
00646:             }
00647:             objectId = result.ObjectId;
00648:             return true;
00649:         }
00650: 
00651:         private static List<InsertedAssetReview> ReadInsertedAssetReviews(Database database)
00652:         {
00653:             var result = new List<InsertedAssetReview>();
00654:             using (Transaction transaction = database.TransactionManager.StartTransaction())
```

### Lines 880-1005
```csharp
00880: 
00881:         private static void EnsureRegApp(Database database, Transaction transaction)
00882:         {
00883:             RegAppTable table = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead) as RegAppTable;
00884:             if (table == null || table.Has(RegAppName)) return;
00885:             table.UpgradeOpen();
00886:             var record = new RegAppTableRecord { Name = RegAppName };
00887:             table.Add(record);
00888:             transaction.AddNewlyCreatedDBObject(record, true);
00889:         }
00890: 
00891:         private static bool PromptText(
00892:             Editor editor,
00893:             string label,
00894:             string defaultValue,
00895:             bool allowSpaces,
00896:             out string value)
00897:         {
00898:             var options = new PromptStringOptions(
00899:                 "\n" + label + (string.IsNullOrWhiteSpace(defaultValue) ? ": " : " <" + defaultValue + ">: "))
00900:             {
00901:                 AllowSpaces = allowSpaces,
00902:                 UseDefaultValue = !string.IsNullOrWhiteSpace(defaultValue),
00903:                 DefaultValue = defaultValue ?? string.Empty
00904:             };
00905:             PromptResult result = editor.GetString(options);
00906:             value = result.Status == PromptStatus.OK
00907:                 ? result.StringResult
00908:                 : result.Status == PromptStatus.None
00909:                     ? defaultValue ?? string.Empty
00910:                     : string.Empty;
00911:             return result.Status != PromptStatus.Cancel;
00912:         }
00913: 
00914:         private static bool PromptPositiveDouble(Editor editor, string label, double defaultValue, out double value)
00915:         {
00916:             var options = new PromptDoubleOptions("\n" + label + " <" + Format(defaultValue) + ">: ")
00917:             {
00918:                 AllowNone = true,
00919:                 AllowNegative = false,
00920:                 AllowZero = false,
00921:                 DefaultValue = defaultValue
00922:             };
00923:             PromptDoubleResult result = editor.GetDouble(options);
00924:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00925:             return result.Status != PromptStatus.Cancel && value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
00926:         }
00927: 
00928:         private static bool PromptDouble(Editor editor, string label, double defaultValue, bool allowNegative, out double value)
00929:         {
00930:             var options = new PromptDoubleOptions("\n" + label + " <" + Format(defaultValue) + ">: ")
00931:             {
00932:                 AllowNone = true,
00933:                 AllowNegative = allowNegative,
00934:                 AllowZero = true,
00935:                 DefaultValue = defaultValue
00936:             };
00937:             PromptDoubleResult result = editor.GetDouble(options);
00938:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00939:             return result.Status != PromptStatus.Cancel && !double.IsNaN(value) && !double.IsInfinity(value);
00940:         }
00941: 
00942:         private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
00943:         {
00944:             var options = new PromptKeywordOptions(
00945:                 "\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00946:             {
00947:                 AllowNone = true
00948:             };
00949:             options.Keywords.Add("Yes");
00950:             options.Keywords.Add("No");
00951:             PromptResult result = editor.GetKeywords(options);
00952:             if (result.Status == PromptStatus.Cancel) return false;
00953:             return result.Status == PromptStatus.None
00954:                 ? defaultValue
00955:                 : Equal(result.StringResult, "Yes");
00956:         }
00957: 
00958:         private static bool PromptExcelPath(Editor editor, string initialName, out string path)
00959:         {
00960:             var options = new PromptSaveFileOptions("\nChoose the Excel workbook path: ")
00961:             {
00962:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
00963:                 DialogCaption = "Export CE Tools Engineering Asset Report",
00964:                 InitialFileName = initialName
00965:             };
00966:             PromptFileNameResult result = editor.GetFileNameForSave(options);
00967:             path = result.Status == PromptStatus.OK
00968:                 ? EnsureExtension(result.StringResult, ".xlsx")
00969:                 : string.Empty;
00970:             return result.Status == PromptStatus.OK;
00971:         }
00972: 
00973:         private static string EnsureExtension(string path, string extension)
00974:         {
00975:             return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
00976:         }
00977: 
00978:         private static string Format(double value)
00979:         {
00980:             return value.ToString("0.###", CultureInfo.CurrentCulture);
00981:         }
00982: 
00983:         private static bool Equal(string left, string right)
00984:         {
00985:             return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
00986:         }
00987: 
00988:         private static Document ActiveDocument()
00989:         {
00990:             return AcApplication.DocumentManager.MdiActiveDocument;
00991:         }
00992:     }
00993: 
00994:     internal sealed class AssetLibrarySettings
00995:     {
00996:         public AssetLibrarySettings(
00997:             string catalogPath,
00998:             double drawingUnitsPerMetre,
00999:             EngineeringAssetApprovalStatus minimumVisibility)
01000:         {
01001:             CatalogPath = catalogPath ?? string.Empty;
01002:             DrawingUnitsPerMetre = drawingUnitsPerMetre;
01003:             MinimumVisibility = minimumVisibility;
01004:         }
01005: 
```

## FeatureLineConstructionCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 212-372
```csharp
00212:             Editor editor = document.Editor;
00213:             PromptSelectionResult selection = GetSelection(
00214:                 editor,
00215:                 "\nSelect feature lines to assign elevations from a surface: ");
00216:             if (selection.Status != PromptStatus.OK)
00217:             {
00218:                 return;
00219:             }
00220: 
00221:             var surfaceOptions = new PromptEntityOptions("\nSelect Civil 3D surface: ");
00222:             surfaceOptions.SetRejectMessage("\nSelect a Civil 3D surface.");
00223:             surfaceOptions.AddAllowedClass(typeof(CivilSurface), false);
00224:             PromptEntityResult surfaceResult = editor.GetEntity(surfaceOptions);
00225:             if (surfaceResult.Status != PromptStatus.OK)
00226:             {
00227:                 return;
00228:             }
00229: 
00230:             var gradeBreakOptions = new PromptKeywordOptions(
00231:                 "\nInsert intermediate surface grade-break points? [Yes/No] <No>: ")
00232:             {
00233:                 AllowNone = true
00234:             };
00235:             gradeBreakOptions.Keywords.Add("Yes");
00236:             gradeBreakOptions.Keywords.Add("No");
00237:             PromptResult gradeBreakResult = editor.GetKeywords(gradeBreakOptions);
00238:             if (gradeBreakResult.Status == PromptStatus.Cancel)
00239:             {
00240:                 return;
00241:             }
00242: 
00243:             bool includeIntermediate =
00244:                 gradeBreakResult.Status == PromptStatus.OK &&
00245:                 string.Equals(gradeBreakResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00246: 
00247:             Database database = document.Database;
00248:             int changed = 0;
00249:             int skipped = 0;
00250: 
00251:             try
00252:             {
00253:                 using (Transaction transaction = database.TransactionManager.StartTransaction())
00254:                 {
00255:                     foreach (SelectedObject selectedObject in selection.Value)
00256:                     {
00257:                         CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
00258:                             transaction,
00259:                             selectedObject,
00260:                             true);
00261: 
00262:                         if (featureLine == null ||
00263:                             featureLine.IsReferenceObject ||
00264:                             IsLayerLocked(transaction, featureLine.LayerId))
00265:                         {
00266:                             skipped++;
00267:                             continue;
00268:                         }
00269: 
00270:                         featureLine.AssignElevationsFromSurface(
00271:                             surfaceResult.ObjectId,
00272:                             includeIntermediate);
00273:                         changed++;
00274:                     }
00275: 
00276:                     transaction.Commit();
00277:                 }
00278: 
00279:                 editor.WriteMessage(
00280:                     "\nCE_FLSURFACE complete. Feature lines updated: {0}; skipped: {1}; intermediate points: {2}.",
00281:                     changed,
00282:                     skipped,
00283:                     includeIntermediate ? "Yes" : "No");
00284:             }
00285:             catch (System.Exception exception)
00286:             {
00287:                 editor.WriteMessage(
00288:                     "\nCE_FLSURFACE cancelled. No changes were committed. {0}",
00289:                     exception.Message);
00290:             }
00291:         }
00292: 
00293:         private static void InsertElevationPoint(Document document)
00294:         {
00295:             Editor editor = document.Editor;
00296:             PromptEntityResult entityResult = PromptForFeatureLine(
00297:                 editor,
00298:                 "\nSelect feature line: ");
00299:             if (entityResult.Status != PromptStatus.OK)
00300:             {
00301:                 return;
00302:             }
00303: 
00304:             PromptPointResult pointResult = editor.GetPoint(
00305:                 "\nPick location along feature line for the new elevation point: ");
00306:             if (pointResult.Status != PromptStatus.OK)
00307:             {
00308:                 return;
00309:             }
00310: 
00311:             var modeOptions = new PromptKeywordOptions(
00312:                 "\nNew point elevation [Interpolate/Elevation] <Interpolate>: ")
00313:             {
00314:                 AllowNone = true
00315:             };
00316:             modeOptions.Keywords.Add("Interpolate");
00317:             modeOptions.Keywords.Add("Elevation");
00318:             PromptResult modeResult = editor.GetKeywords(modeOptions);
00319:             if (modeResult.Status == PromptStatus.Cancel)
00320:             {
00321:                 return;
00322:             }
00323: 
00324:             bool useEnteredElevation =
00325:                 modeResult.Status == PromptStatus.OK &&
00326:                 string.Equals(modeResult.StringResult, "Elevation", StringComparison.OrdinalIgnoreCase);
00327: 
00328:             double enteredElevation = 0.0;
00329:             if (useEnteredElevation)
00330:             {
00331:                 PromptDoubleResult elevationResult = editor.GetDouble(
00332:                     new PromptDoubleOptions("\nEnter elevation for the new point: ")
00333:                     {
00334:                         AllowNegative = true,
00335:                         AllowZero = true,
00336:                         AllowNone = false
00337:                     });
00338:                 if (elevationResult.Status != PromptStatus.OK)
00339:                 {
00340:                     return;
00341:                 }
00342: 
00343:                 enteredElevation = elevationResult.Value;
00344:             }
00345: 
00346:             Database database = document.Database;
00347:             Point3d pickedPoint = pointResult.Value.TransformBy(editor.CurrentUserCoordinateSystem);
00348: 
00349:             try
00350:             {
00351:                 using (Transaction transaction = database.TransactionManager.StartTransaction())
00352:                 {
00353:                     CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
00354:                         transaction,
00355:                         entityResult.ObjectId,
00356:                         true);
00357:                     EnsureEditable(transaction, featureLine);
00358: 
00359:                     Point3d pointOnFeatureLine = featureLine.GetClosestPointTo(pickedPoint, false);
00360:                     featureLine.InsertElevationPoint(pointOnFeatureLine);
00361: 
00362:                     if (useEnteredElevation)
00363:                     {
00364:                         Point3dCollection allPoints = featureLine.GetPoints(FeatureLinePointType.AllPoints);
00365:                         int index = FindClosestPointIndex(allPoints, pointOnFeatureLine);
00366:                         featureLine.SetPointElevation(index, enteredElevation);
00367:                     }
00368: 
00369:                     transaction.Commit();
00370: 
00371:                     editor.WriteMessage(
00372:                         "\nCE_FLINSERT complete at X={0:N3}, Y={1:N3}, Z={2:N3}.",
```

### Lines 416-500
```csharp
00416:                 {
00417:                     editor.WriteMessage("\nThe selected object is not an ordinary feature line.");
00418:                     return;
00419:                 }
00420: 
00421:                 Point3dCollection elevationPoints = featureLine.GetPoints(
00422:                     FeatureLinePointType.ElevationPoint);
00423:                 if (elevationPoints == null || elevationPoints.Count == 0)
00424:                 {
00425:                     editor.WriteMessage("\nThe feature line has no removable elevation points.");
00426:                     return;
00427:                 }
00428: 
00429:                 int index = FindClosestPointIndex(elevationPoints, pickedPoint);
00430:                 nearestPoint = elevationPoints[index];
00431:                 nearestDistance = PlanDistance(nearestPoint, pickedPoint);
00432:             }
00433: 
00434:             var confirmOptions = new PromptKeywordOptions(
00435:                 string.Format(
00436:                     "\nDelete elevation point at X={0:N3}, Y={1:N3}, Z={2:N3} (pick distance {3:N3})? [Yes/No] <No>: ",
00437:                     nearestPoint.X,
00438:                     nearestPoint.Y,
00439:                     nearestPoint.Z,
00440:                     nearestDistance))
00441:             {
00442:                 AllowNone = true
00443:             };
00444:             confirmOptions.Keywords.Add("Yes");
00445:             confirmOptions.Keywords.Add("No");
00446:             PromptResult confirmResult = editor.GetKeywords(confirmOptions);
00447:             if (confirmResult.Status != PromptStatus.OK ||
00448:                 !string.Equals(confirmResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
00449:             {
00450:                 editor.WriteMessage("\nCE_FLDELETE cancelled.");
00451:                 return;
00452:             }
00453: 
00454:             try
00455:             {
00456:                 using (Transaction transaction = database.TransactionManager.StartTransaction())
00457:                 {
00458:                     CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
00459:                         transaction,
00460:                         entityResult.ObjectId,
00461:                         true);
00462:                     EnsureEditable(transaction, featureLine);
00463:                     featureLine.DeleteElevationPoint(nearestPoint);
00464:                     transaction.Commit();
00465:                 }
00466: 
00467:                 editor.WriteMessage("\nCE_FLDELETE complete.");
00468:             }
00469:             catch (System.Exception exception)
00470:             {
00471:                 editor.WriteMessage(
00472:                     "\nCE_FLDELETE cancelled. No changes were committed. {0}",
00473:                     exception.Message);
00474:             }
00475:         }
00476: 
00477:         private static PromptSelectionResult GetSelection(Editor editor, string message)
00478:         {
00479:             PromptSelectionResult implied = editor.SelectImplied();
00480:             if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
00481:             {
00482:                 editor.SetImpliedSelection(new ObjectId[0]);
00483:                 return implied;
00484:             }
00485: 
00486:             return editor.GetSelection(
00487:                 new PromptSelectionOptions
00488:                 {
00489:                     MessageForAdding = message,
00490:                     AllowDuplicates = false,
00491:                     RejectObjectsFromNonCurrentSpace = true
00492:                 });
00493:         }
00494: 
00495:         private static PromptEntityResult PromptForFeatureLine(Editor editor, string message)
00496:         {
00497:             var options = new PromptEntityOptions(message);
00498:             options.SetRejectMessage("\nSelect an ordinary Civil 3D feature line.");
00499:             options.AddAllowedClass(typeof(CivilFeatureLine), false);
00500:             return editor.GetEntity(options);
```

## FeatureLineRelativeCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 964-1022
```csharp
00964:             return best;
00965:         }
00966: 
00967:         private static double PlanDistance(Point3d first, Point3d second)
00968:         {
00969:             double dx = first.X - second.X;
00970:             double dy = first.Y - second.Y;
00971:             return Math.Sqrt((dx * dx) + (dy * dy));
00972:         }
00973: 
00974:         private static void Dispose(DBObjectCollection collection)
00975:         {
00976:             if (collection == null) return;
00977:             foreach (DBObject item in collection) item?.Dispose();
00978:         }
00979: 
00980:         private static bool Confirm(Editor editor, string message)
00981:         {
00982:             var options = new PromptKeywordOptions(
00983:                 "\n" + message + "? [Yes/No] <No>: ")
00984:             {
00985:                 AllowNone = true
00986:             };
00987:             options.Keywords.Add("Yes");
00988:             options.Keywords.Add("No");
00989:             PromptResult result = editor.GetKeywords(options);
00990:             return result.Status == PromptStatus.OK &&
00991:                    result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
00992:         }
00993: 
00994:         private sealed class Relation
00995:         {
00996:             public Relation(string sourceHandle, double horizontalOffset, double verticalOffset, int sequence)
00997:             {
00998:                 SourceHandle = sourceHandle;
00999:                 HorizontalOffset = horizontalOffset;
01000:                 VerticalOffset = verticalOffset;
01001:                 Sequence = sequence;
01002:             }
01003:             public string SourceHandle { get; }
01004:             public double HorizontalOffset { get; }
01005:             public double VerticalOffset { get; }
01006:             public int Sequence { get; }
01007:         }
01008: 
01009:         private sealed class ChildRecord
01010:         {
01011:             public ChildRecord(ObjectId objectId, string name, Relation relation)
01012:             {
01013:                 ObjectId = objectId;
01014:                 Name = name;
01015:                 Relation = relation;
01016:             }
01017:             public ObjectId ObjectId { get; }
01018:             public string Name { get; }
01019:             public Relation Relation { get; }
01020:         }
01021:     }
01022: }
```

## FeatureLineWeedCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 82-165
```csharp
00082:                 : spacingResult.Value;
00083: 
00084:             PreviewResult preview = BuildPreview(
00085:                 document.Database,
00086:                 selection,
00087:                 verticalTolerance,
00088:                 minimumSpacing);
00089: 
00090:             if (preview.CandidatePoints == 0)
00091:             {
00092:                 editor.WriteMessage(
00093:                     "\nCE_FLWEED preview: no removable elevation points found. " +
00094:                     "Feature lines checked: {0}; skipped: {1}.",
00095:                     preview.FeatureLinesChecked,
00096:                     preview.FeatureLinesSkipped);
00097:                 return;
00098:             }
00099: 
00100:             var confirmOptions = new PromptKeywordOptions(
00101:                 string.Format(
00102:                     "\nCE_FLWEED preview: remove {0} elevation points from {1} feature lines? [Yes/No] <No>: ",
00103:                     preview.CandidatePoints,
00104:                     preview.CandidateFeatureLines))
00105:             {
00106:                 AllowNone = true
00107:             };
00108:             confirmOptions.Keywords.Add("Yes");
00109:             confirmOptions.Keywords.Add("No");
00110: 
00111:             PromptResult confirmResult = editor.GetKeywords(confirmOptions);
00112:             if (confirmResult.Status != PromptStatus.OK ||
00113:                 !string.Equals(confirmResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
00114:             {
00115:                 editor.WriteMessage("\nCE_FLWEED cancelled. No changes were made.");
00116:                 return;
00117:             }
00118: 
00119:             ApplyWeeding(
00120:                 document,
00121:                 selection,
00122:                 verticalTolerance,
00123:                 minimumSpacing);
00124:         }
00125: 
00126:         private static PreviewResult BuildPreview(
00127:             Database database,
00128:             PromptSelectionResult selection,
00129:             double verticalTolerance,
00130:             double minimumSpacing)
00131:         {
00132:             var result = new PreviewResult();
00133: 
00134:             using (Transaction transaction = database.TransactionManager.StartTransaction())
00135:             {
00136:                 foreach (SelectedObject selectedObject in selection.Value)
00137:                 {
00138:                     CivilFeatureLine featureLine = OpenOrdinaryFeatureLine(
00139:                         transaction,
00140:                         selectedObject,
00141:                         false);
00142: 
00143:                     if (featureLine == null ||
00144:                         featureLine.IsReferenceObject ||
00145:                         featureLine.Closed ||
00146:                         IsLayerLocked(transaction, featureLine.LayerId))
00147:                     {
00148:                         result.FeatureLinesSkipped++;
00149:                         continue;
00150:                     }
00151: 
00152:                     result.FeatureLinesChecked++;
00153:                     Point3dCollection candidates = FindCandidates(
00154:                         featureLine,
00155:                         verticalTolerance,
00156:                         minimumSpacing);
00157: 
00158:                     if (candidates.Count > 0)
00159:                     {
00160:                         result.CandidateFeatureLines++;
00161:                         result.CandidatePoints += candidates.Count;
00162:                     }
00163:                 }
00164:             }
00165: 
```

## FeatureProfileSurfaceCommentCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 826-905
```csharp
00826:             if (value == null) return 0.0;
00827:             foreach (string propertyName in propertyNames)
00828:             {
00829:                 try
00830:                 {
00831:                     PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
00832:                     object raw = property == null ? null : property.GetValue(value, null);
00833:                     if (raw == null) continue;
00834:                     double number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
00835:                     if (!double.IsNaN(number) && !double.IsInfinity(number)) return number;
00836:                 }
00837:                 catch { }
00838:             }
00839:             return 0.0;
00840:         }
00841: 
00842:         private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
00843:         {
00844:             var options = new PromptKeywordOptions(
00845:                 "\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00846:             {
00847:                 AllowNone = true
00848:             };
00849:             options.Keywords.Add("Yes");
00850:             options.Keywords.Add("No");
00851:             PromptResult result = editor.GetKeywords(options);
00852:             if (result.Status == PromptStatus.Cancel) return false;
00853:             return result.Status == PromptStatus.None
00854:                 ? defaultValue
00855:                 : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00856:         }
00857: 
00858:         internal static ObjectId CreateCoordinateAnchor(
00859:             Database database,
00860:             Point3d point)
00861:         {
00862:             try
00863:             {
00864:                 using (Transaction transaction =
00865:                     database.TransactionManager.StartTransaction())
00866:                 {
00867:                     BlockTableRecord currentSpace =
00868:                         transaction.GetObject(
00869:                             database.CurrentSpaceId,
00870:                             OpenMode.ForWrite,
00871:                             false) as BlockTableRecord;
00872:                     if (currentSpace == null) return ObjectId.Null;
00873:                     var anchor = new DBPoint(point);
00874:                     anchor.SetDatabaseDefaults(database);
00875:                     ObjectId anchorId = currentSpace.AppendEntity(anchor);
00876:                     transaction.AddNewlyCreatedDBObject(anchor, true);
00877:                     transaction.Commit();
00878:                     return anchorId;
00879:                 }
00880:             }
00881:             catch
00882:             {
00883:                 return ObjectId.Null;
00884:             }
00885:         }
00886: 
00887:         private sealed class FeatureVertexWork
00888:         {
00889:             public FeatureVertexWork(
00890:                 ObjectId featureLineId,
00891:                 int vertexIndex,
00892:                 Point3d target,
00893:                 Point3d label,
00894:                 string pointName,
00895:                 string contents,
00896:                 string plain)
00897:             {
00898:                 FeatureLineId = featureLineId;
00899:                 VertexIndex = vertexIndex;
00900:                 Target = target;
00901:                 Label = label;
00902:                 PointName = pointName;
00903:                 Contents = contents;
00904:                 Plain = plain;
00905:             }
```

## FloatingToolsWindow.cs
Hits: `CE_PROJECTSETUP`

### Lines 1076-1148
```csharp
01076:         public List<WorkflowStep> Steps { get; private set; }
01077:     }
01078: 
01079:     internal static class WorkflowCatalog
01080:     {
01081:         public static IEnumerable<WorkflowDefinition> Create(
01082:             IList<FloatingToolDefinition> tools)
01083:         {
01084:             yield return Build(
01085:                 "all", "All", "All CE Tools Commands",
01086:                 "Search and launch every CE Tools command declared by the loaded plug-in, including specialist commands that are not pinned to the ribbon.",
01087:                 tools, null);
01088: 
01089:             yield return Build(
01090:                 "general", "General", "General Workflow",
01091:                 "Start with project information and standards, coordinate the discipline models, refresh linked data, produce quantities and issue reports.",
01092:                 tools, new[] { "PROJECT", "STANDARD", "PRESENTATION", "REPORT", "BOQ", "REFRESH", "XREF", "MODEL", "DRAW", "DETAIL", "ASSET" },
01093:                 Step("Open Phase 1 utilities", "CE_PHASE1"),
01094:                 Step("Project setup", "CE_PROJECTSETUP"),
01095:                 Step("Project standards", "CE_STANDARDSELECT"),
01096:                 Step("Refresh linked outputs", "CE_REFRESHALL"),
01097:                 Step("Review refresh status", "CE_REFRESHSTATUS"),
01098:                 Step("Configure automatic refresh", "CE_AUTOREFRESH"),
01099:                 Step("Create BOQs", "CE_BOQTOOLS"),
01100:                 Step("Generate reports", "CE_PRESENTATIONTOOLS"));
01101: 
01102:             yield return Build(
01103:                 "survey", "Survey", "Survey Workflow",
01104:                 "Set the drawing coordinate system, create linked survey points and crosses, generate polyline-vertex COGO points and coordinate tables, then refresh linked outputs after survey edits.",
01105:                 tools, new[] { "SURVEY", "COORD", "COGO", "PLDIR" },
01106:                 Step("Set coordinate system", "CE_COORDSYSASSIGN"),
01107:                 Step("Open survey cleanup", "CE_SURVEYCLEANUP"),
01108:                 Step("Create linked point", "CE_COORDPICK2"),
01109:                 Step("Create coordinate cross", "CE_COORDCROSS2"),
01110:                 Step("Create vertex points", "CE_COORDPOLY2"),
01111:                 Step("Create coordinate table", "CE_COORDTABLE2"),
01112:                 Step("Refresh linked coordinates", "CE_COORDREFRESH"),
01113:                 Step("Show polyline direction", "CE_PLDIR"));
01114: 
01115:             yield return Build(
01116:                 "roads", "Roads", "Roads Workflow",
01117:                 "Create and style road alignments, profiles and corridors; generate cross sections, quantities and production outputs.",
01118:                 tools, new[] { "ROAD", "ALIGN", "CORRIDOR", "PROFILEVIEW", "CROSS", "SECTION", "INTERSECTION", "PARK", "BOQ" },
01119:                 Step("Open Road Production workflow", "CE_ROADPRODUCTION"),
01120:                 Step("Create alignments", "CE_ROADALIGN"),
01121:                 Step("Create profiles", "CE_ROADPROFILES"),
01122:                 Step("Create or review assembly", "CE_ASSEMBLYTOOLS"),
01123:                 Step("Create corridors", "CE_ROADCORRIDORS"),
01124:                 Step("Create cross sections", "CE_CROSSSECTION"),
01125:                 Step("Create BOQ", "CE_BOQROAD"),
01126:                 Step("Generate report", "CE_PRESENTATIONTOOLS"));
01127: 
01128:             yield return Build(
01129:                 "stormwater", "Stormwater", "Stormwater Workflow",
01130:                 "Delineate catchments, review hydrology, create the stormwater network and profiles, then produce quantities and reports.",
01131:                 tools, new[] { "SW", "STORM", "HYDRO", "CATCHMENT", "CULVERT", "FLOOD", "BOQ" },
01132:                 Step("Open Stormwater workflow", "CE_SWTOOLS"),
01133:                 Step("Review surface hydrology", "CE_HYDROLOGYTOOLS"),
01134:                 Step("Sequence network", "CE_SWSEQ"),
01135:                 Step("Create alignments", "CE_SWALIGN"),
01136:                 Step("Create profile views", "CE_SWPROFILE"),
01137:                 Step("Configure production settings", "CE_SWSETTINGS"),
01138:                 Step("Create BOQ", "CE_BOQSTORM"),
01139:                 Step("Generate report", "CE_PRESENTATIONTOOLS"));
01140: 
01141:             yield return Build(
01142:                 "sewer", "Sewer", "Sewer Workflow",
01143:                 "Sequence sewer branches, create linked alignments and profiles, validate the network, then create the sewer BOQ and report.",
01144:                 tools, new[] { "SEWER", "SEW", "BRANCH", "NETWORK", "PROFILE", "BOQ" },
01145:                 Step("Open Sewer workflow", "CE_SEWTOOLS", "CE_SEWTOOLS"),
01146:                 Step("Sequence network", "CE_SEWSEQ", "CE_SEWSEQ", "CE_SEWLABELS"),
01147:                 Step("Create alignments", "CE_SEWALIGN", "CE_SEWALIGN", "CE_SEWREFRESH", "CE_SEWFORMAT"),
01148:                 Step("Create profile views", "CE_SEWPROFILE"),
```

## FloodResultReviewCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 575-637
```csharp
00575:                 LowerLimit = 1, UpperLimit = count, DefaultValue = 1, UseDefaultValue = true
00576:             };
00577:             PromptIntegerResult result = editor.GetInteger(options);
00578:             index = (result.Status == PromptStatus.OK ? result.Value : 1) - 1;
00579:             return result.Status != PromptStatus.Cancel && index >= 0 && index < count;
00580:         }
00581: 
00582:         private static bool PromptNonNegativeDouble(Editor editor, string label, double defaultValue, out double value)
00583:         {
00584:             var options = new PromptDoubleOptions("\n" + label + " <" + defaultValue.ToString(CultureInfo.CurrentCulture) + ">: ")
00585:             { AllowNone = true, AllowNegative = false, AllowZero = true, DefaultValue = defaultValue };
00586:             PromptDoubleResult result = editor.GetDouble(options);
00587:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00588:             return result.Status != PromptStatus.Cancel && value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
00589:         }
00590: 
00591:         private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
00592:         {
00593:             var options = new PromptKeywordOptions("\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ") { AllowNone = true };
00594:             options.Keywords.Add("Yes"); options.Keywords.Add("No");
00595:             PromptResult result = editor.GetKeywords(options);
00596:             return result.Status != PromptStatus.Cancel &&
00597:                 (result.Status == PromptStatus.None ? defaultValue : Equal(result.StringResult, "Yes"));
00598:         }
00599: 
00600:         private static bool PromptSavePath(Editor editor, string filter, string initialName, string extension, out string path)
00601:         {
00602:             var options = new PromptSaveFileOptions("\nChoose the output file path: ")
00603:             { Filter = filter, DialogCaption = "CE Tools Flood Result Output", InitialFileName = initialName };
00604:             PromptFileNameResult result = editor.GetFileNameForSave(options);
00605:             path = result.Status == PromptStatus.OK
00606:                 ? (result.StringResult.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? result.StringResult : result.StringResult + extension)
00607:                 : string.Empty;
00608:             return result.Status == PromptStatus.OK;
00609:         }
00610: 
00611:         private static string Format(double? value)
00612:         {
00613:             return value.HasValue ? value.Value.ToString("0.###", CultureInfo.CurrentCulture) : string.Empty;
00614:         }
00615: 
00616:         private static bool Equal(string first, string second)
00617:         {
00618:             return string.Equals(
00619:                 string.IsNullOrWhiteSpace(first) ? "<Unspecified>" : first.Trim(),
00620:                 string.IsNullOrWhiteSpace(second) ? "<Unspecified>" : second.Trim(),
00621:                 StringComparison.OrdinalIgnoreCase);
00622:         }
00623: 
00624:         private static Document ActiveDocument()
00625:         {
00626:             return AcApplication.DocumentManager.MdiActiveDocument;
00627:         }
00628:     }
00629: 
00630:     internal sealed class FloodResultEntity
00631:     {
00632:         public FloodResultEntity(ObjectId objectId, FloodResultPoint point)
00633:         { ObjectId = objectId; Point = point; }
00634:         public ObjectId ObjectId { get; private set; }
00635:         public FloodResultPoint Point { get; private set; }
00636:     }
00637: }
```

## FlowNetworkCulvertCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 161-240
```csharp
00161:                 Point3d end = SurfaceHydrologyCommands.CellPoint(sample, downstream, true);
00162:                 double dx = end.X - start.X;
00163:                 double dy = end.Y - start.Y;
00164:                 double length = Math.Sqrt(dx * dx + dy * dy);
00165:                 if (length <= Tolerance) continue;
00166:                 result.Add(new MajorFlowEdge(
00167:                     index,
00168:                     downstream,
00169:                     start,
00170:                     end,
00171:                     length,
00172:                     sample.Analysis.AccumulationArea[index]));
00173:             }
00174:             return result;
00175:         }
00176: 
00177:         private static List<ObjectId> PromptCrossingCurves(Editor editor)
00178:         {
00179:             var options = new PromptKeywordOptions(
00180:                 "\nSelect road/kerb/centreline curves for crossing screening [Yes/No] <Yes>: ")
00181:             {
00182:                 AllowNone = true
00183:             };
00184:             options.Keywords.Add("Yes");
00185:             options.Keywords.Add("No");
00186:             PromptResult result = editor.GetKeywords(options);
00187:             if (result.Status == PromptStatus.Cancel) return null;
00188:             bool select = result.Status == PromptStatus.None ||
00189:                 string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00190:             if (!select) return new List<ObjectId>();
00191: 
00192:             PromptSelectionResult selection = editor.GetSelection(
00193:                 new PromptSelectionOptions
00194:                 {
00195:                     MessageForAdding = "\nSelect road, kerb or centreline curves: "
00196:                 });
00197:             if (selection.Status == PromptStatus.Cancel) return null;
00198:             if (selection.Status != PromptStatus.OK) return new List<ObjectId>();
00199:             return selection.Value.GetObjectIds().ToList();
00200:         }
00201: 
00202:         private static List<PlanSegment> ReadCrossingSegments(
00203:             Database database,
00204:             IEnumerable<ObjectId> ids,
00205:             double gridSpacing)
00206:         {
00207:             var result = new List<PlanSegment>();
00208:             using (Transaction transaction =
00209:                 database.TransactionManager.StartTransaction())
00210:             {
00211:                 foreach (ObjectId id in ids)
00212:                 {
00213:                     Curve curve;
00214:                     try
00215:                     {
00216:                         curve = transaction.GetObject(
00217:                             id,
00218:                             OpenMode.ForRead,
00219:                             false) as Curve;
00220:                     }
00221:                     catch
00222:                     {
00223:                         continue;
00224:                     }
00225:                     if (curve == null) continue;
00226:                     LayerTableRecord layer = transaction.GetObject(
00227:                         curve.LayerId,
00228:                         OpenMode.ForRead,
00229:                         false) as LayerTableRecord;
00230:                     if (layer != null && layer.IsLocked) continue;
00231:                     List<Point3d> points = SampleCurve(
00232:                         curve,
00233:                         Math.Max(gridSpacing * 0.25, Tolerance));
00234:                     for (int index = 1; index < points.Count; index++)
00235:                     {
00236:                         Point3d first = points[index - 1];
00237:                         Point3d second = points[index];
00238:                         if (PlanDistance(first, second) <= Tolerance) continue;
00239:                         result.Add(new PlanSegment(
00240:                             id,
```

## GradingDrainageDiagnosticCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 19-99
```csharp
00019: {
00020:     /// <summary>
00021:     /// Non-destructive grading diagnostics. Source geometry is never edited;
00022:     /// generated review lines, circles and labels can be cleared independently.
00023:     /// </summary>
00024:     public sealed class GradingDrainageDiagnosticCommands
00025:     {
00026:         private const string RegAppName = "CE_GRADING_REVIEW";
00027:         private const string LowSlopeLayer = "CE-REVIEW-LOW-SLOPE";
00028:         private const string LowPointLayer = "CE-REVIEW-LOW-POINT";
00029:         private const double GeometryTolerance = 0.000001;
00030: 
00031:         [CommandMethod("CE_TOOLS", "CE_GRADINGDIAGNOSTICS", CommandFlags.Modal)]
00032:         public void GradingDiagnostics()
00033:         {
00034:             Document document = ActiveDocument();
00035:             if (document == null) return;
00036: 
00037:             var options = new PromptKeywordOptions(
00038:                 "\nGrading diagnostics [LowSlope/LowPoints/Clear] <LowSlope>: ")
00039:             {
00040:                 AllowNone = true
00041:             };
00042:             options.Keywords.Add("LowSlope");
00043:             options.Keywords.Add("LowPoints");
00044:             options.Keywords.Add("Clear");
00045:             PromptResult result = document.Editor.GetKeywords(options);
00046:             if (result.Status == PromptStatus.Cancel) return;
00047:             string command = result.Status == PromptStatus.OK
00048:                 ? result.StringResult
00049:                 : "LowSlope";
00050:             if (string.Equals(command, "LowPoints", StringComparison.OrdinalIgnoreCase))
00051:                 document.SendStringToExecute("CE_LOWPOINTS ", true, false, true);
00052:             else if (string.Equals(command, "Clear", StringComparison.OrdinalIgnoreCase))
00053:                 document.SendStringToExecute("CE_GRADINGREVIEWCLEAR ", true, false, true);
00054:             else
00055:                 document.SendStringToExecute("CE_LOWSLOPE ", true, false, true);
00056:         }
00057: 
00058:         [CommandMethod(
00059:             "CE_TOOLS",
00060:             "CE_LOWSLOPE",
00061:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00062:         public void HighlightLowSlopes()
00063:         {
00064:             Document document = ActiveDocument();
00065:             if (document == null) return;
00066:             Editor editor = document.Editor;
00067: 
00068:             double threshold;
00069:             if (!PromptPositiveDouble(
00070:                     editor,
00071:                     "Minimum acceptable absolute grade (%)",
00072:                     0.5,
00073:                     out threshold))
00074:             {
00075:                 return;
00076:             }
00077: 
00078:             PromptSelectionResult selection = GetSelection(
00079:                 editor,
00080:                 "\nSelect feature lines, lines, 2D polylines or 3D polylines to analyse: ");
00081:             if (selection.Status != PromptStatus.OK) return;
00082: 
00083:             AnnotationOptions annotation;
00084:             if (!AnnotationSettingsStore.Prepare(document, false, out annotation))
00085:                 return;
00086: 
00087:             List<SlopeObservation> observations = ReadSlopeObservations(
00088:                 document.Database,
00089:                 selection);
00090:             List<SlopeObservation> low = observations
00091:                 .Where(item => Math.Abs(item.GradePercent) < threshold)
00092:                 .ToList();
00093:             if (low.Count == 0)
00094:             {
00095:                 editor.WriteMessage(
00096:                     "\nCE_LOWSLOPE complete. Analysed segments={0}; none were below {1:N3}%.",
00097:                     observations.Count,
00098:                     threshold);
00099:                 return;
```

## HatchCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 672-744
```csharp
00672:                 hatch.PatternName,
00673:                 hatch.PatternScale > 0.0 ? hatch.PatternScale : DefaultScale,
00674:                 hatch.PatternAngle,
00675:                 colour,
00676:                 ToTransparencyPercent(hatch.Transparency),
00677:                 hatch.HatchStyle);
00678:         }
00679: 
00680:         private static bool PromptForSettings(
00681:             Editor editor,
00682:             HatchVisualSettings current,
00683:             out HatchVisualSettings settings)
00684:         {
00685:             settings = null;
00686: 
00687:             string patternDefault = string.IsNullOrWhiteSpace(current.PatternName)
00688:                 ? DefaultPattern
00689:                 : current.PatternName;
00690:             PromptResult patternResult = editor.GetString(new PromptStringOptions(
00691:                 "\nHatch pattern name <" + patternDefault + ">: ")
00692:             {
00693:                 AllowSpaces = false,
00694:                 UseDefaultValue = true,
00695:                 DefaultValue = patternDefault
00696:             });
00697:             if (patternResult.Status != PromptStatus.OK)
00698:             {
00699:                 return false;
00700:             }
00701: 
00702:             string pattern = string.IsNullOrWhiteSpace(patternResult.StringResult)
00703:                 ? patternDefault
00704:                 : patternResult.StringResult.Trim();
00705: 
00706:             PromptDoubleResult scaleResult = editor.GetDouble(new PromptDoubleOptions(
00707:                 "\nHatch scale <" + current.PatternScale.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
00708:             {
00709:                 AllowNegative = false,
00710:                 AllowZero = false,
00711:                 UseDefaultValue = true,
00712:                 DefaultValue = current.PatternScale > 0.0
00713:                     ? current.PatternScale
00714:                     : DefaultScale
00715:             });
00716:             if (scaleResult.Status != PromptStatus.OK)
00717:             {
00718:                 return false;
00719:             }
00720: 
00721:             double angleDefault = RadiansToDegrees(current.PatternAngle);
00722:             PromptDoubleResult angleResult = editor.GetDouble(new PromptDoubleOptions(
00723:                 "\nHatch angle in degrees <" + angleDefault.ToString("0.##", CultureInfo.InvariantCulture) + ">: ")
00724:             {
00725:                 AllowNegative = true,
00726:                 AllowZero = true,
00727:                 UseDefaultValue = true,
00728:                 DefaultValue = angleDefault
00729:             });
00730:             if (angleResult.Status != PromptStatus.OK)
00731:             {
00732:                 return false;
00733:             }
00734: 
00735:             PromptIntegerResult colourResult = editor.GetInteger(new PromptIntegerOptions(
00736:                 "\nACI colour 1-255 <" + current.ColourIndex.ToString(CultureInfo.InvariantCulture) + ">: ")
00737:             {
00738:                 AllowNegative = false,
00739:                 AllowZero = false,
00740:                 LowerLimit = 1,
00741:                 UpperLimit = 255,
00742:                 UseDefaultValue = true,
00743:                 DefaultValue = current.ColourIndex >= 1 && current.ColourIndex <= 255
00744:                     ? current.ColourIndex
```

### Lines 867-928
```csharp
00867:             return string.Equals(
00868:                 patternName,
00869:                 "SOLID",
00870:                 StringComparison.OrdinalIgnoreCase);
00871:         }
00872: 
00873:         private static double DegreesToRadians(double degrees)
00874:         {
00875:             return degrees * Math.PI / 180.0;
00876:         }
00877: 
00878:         private static double RadiansToDegrees(double radians)
00879:         {
00880:             return radians * 180.0 / Math.PI;
00881:         }
00882: 
00883:         private static bool Confirm(Editor editor, string message)
00884:         {
00885:             var options = new PromptKeywordOptions(
00886:                 "\n" + message + "? [Yes/No] <No>: ")
00887:             {
00888:                 AllowNone = true
00889:             };
00890:             options.Keywords.Add("Yes");
00891:             options.Keywords.Add("No");
00892:             PromptResult result = editor.GetKeywords(options);
00893:             return result.Status == PromptStatus.OK &&
00894:                    result.StringResult.Equals(
00895:                        "Yes",
00896:                        StringComparison.OrdinalIgnoreCase);
00897:         }
00898: 
00899:         private sealed class HatchVisualSettings
00900:         {
00901:             public HatchVisualSettings(
00902:                 HatchPatternType patternType,
00903:                 string patternName,
00904:                 double patternScale,
00905:                 double patternAngle,
00906:                 int colourIndex,
00907:                 int transparencyPercent,
00908:                 HatchStyle hatchStyle)
00909:             {
00910:                 PatternType = patternType;
00911:                 PatternName = patternName;
00912:                 PatternScale = patternScale;
00913:                 PatternAngle = patternAngle;
00914:                 ColourIndex = colourIndex;
00915:                 TransparencyPercent = transparencyPercent;
00916:                 HatchStyle = hatchStyle;
00917:             }
00918: 
00919:             public HatchPatternType PatternType { get; }
00920:             public string PatternName { get; }
00921:             public double PatternScale { get; }
00922:             public double PatternAngle { get; }
00923:             public int ColourIndex { get; }
00924:             public int TransparencyPercent { get; }
00925:             public HatchStyle HatchStyle { get; }
00926:         }
00927:     }
00928: }
```

## HydraulicReviewCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 108-187
```csharp
00108: 
00109:             if (PromptYesNo(editor, "Export the rational-flow scenarios to Excel", false))
00110:             {
00111:                 ExportRationalScenarios(editor, areaHa, coefficient, scenarios);
00112:             }
00113:         }
00114: 
00115:         [CommandMethod("CE_TOOLS", "CE_CULVERTREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
00116:         public void CulvertCapacityReview()
00117:         {
00118:             Document document = ActiveDocument();
00119:             if (document == null) return;
00120:             Editor editor = document.Editor;
00121: 
00122:             double designFlow;
00123:             if (!PromptPositiveDouble(editor, "Design flow (m³/s)", 1.0, out designFlow))
00124:                 return;
00125: 
00126:             var typeOptions = new PromptKeywordOptions(
00127:                 "\nCulvert type [Circular/Box] <Circular>: ")
00128:             {
00129:                 AllowNone = true
00130:             };
00131:             typeOptions.Keywords.Add("Circular");
00132:             typeOptions.Keywords.Add("Box");
00133:             PromptResult typeResult = editor.GetKeywords(typeOptions);
00134:             if (typeResult.Status == PromptStatus.Cancel) return;
00135:             bool circular = typeResult.Status != PromptStatus.OK ||
00136:                 string.Equals(typeResult.StringResult, "Circular", StringComparison.OrdinalIgnoreCase);
00137: 
00138:             double width;
00139:             double height;
00140:             if (circular)
00141:             {
00142:                 if (!PromptPositiveDouble(editor, "Internal diameter (m)", 0.9, out width))
00143:                     return;
00144:                 height = width;
00145:             }
00146:             else
00147:             {
00148:                 if (!PromptPositiveDouble(editor, "Internal width (m)", 1.2, out width))
00149:                     return;
00150:                 if (!PromptPositiveDouble(editor, "Internal height (m)", 0.9, out height))
00151:                     return;
00152:             }
00153: 
00154:             int barrels;
00155:             if (!PromptPositiveInteger(editor, "Number of barrels", 1, out barrels))
00156:                 return;
00157:             double roughness;
00158:             double slopePercent;
00159:             if (!PromptPositiveDouble(editor, "Manning roughness n", 0.013, out roughness))
00160:                 return;
00161:             if (!PromptPositiveDouble(editor, "Culvert slope (%)", 1.0, out slopePercent))
00162:                 return;
00163: 
00164:             double area;
00165:             double wettedPerimeter;
00166:             if (circular)
00167:             {
00168:                 area = Math.PI * width * width / 4.0;
00169:                 wettedPerimeter = Math.PI * width;
00170:             }
00171:             else
00172:             {
00173:                 area = width * height;
00174:                 wettedPerimeter = width + (2.0 * height);
00175:             }
00176:             double hydraulicRadius = area / wettedPerimeter;
00177:             double slope = slopePercent / 100.0;
00178:             double singleCapacity =
00179:                 (1.0 / roughness) *
00180:                 area *
00181:                 Math.Pow(hydraulicRadius, 2.0 / 3.0) *
00182:                 Math.Sqrt(slope);
00183:             double totalCapacity = singleCapacity * barrels;
00184:             double velocity = singleCapacity / area;
00185:             int requiredBarrels = singleCapacity > GeometryTolerance
00186:                 ? (int)Math.Ceiling(designFlow / singleCapacity)
00187:                 : int.MaxValue;
```

### Lines 764-843
```csharp
00764:             out int value)
00765:         {
00766:             var options = new PromptIntegerOptions(
00767:                 "\n" + name + " <" + defaultValue.ToString(CultureInfo.InvariantCulture) + ">: ")
00768:             {
00769:                 AllowNone = true,
00770:                 AllowNegative = false,
00771:                 AllowZero = false,
00772:                 DefaultValue = defaultValue,
00773:                 UseDefaultValue = true
00774:             };
00775:             PromptIntegerResult result = editor.GetInteger(options);
00776:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00777:             return result.Status == PromptStatus.OK;
00778:         }
00779: 
00780:         private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
00781:         {
00782:             var options = new PromptKeywordOptions(
00783:                 "\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00784:             {
00785:                 AllowNone = true
00786:             };
00787:             options.Keywords.Add("Yes");
00788:             options.Keywords.Add("No");
00789:             PromptResult result = editor.GetKeywords(options);
00790:             if (result.Status == PromptStatus.Cancel) return false;
00791:             return result.Status == PromptStatus.None
00792:                 ? defaultValue
00793:                 : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00794:         }
00795: 
00796:         private static double NormalizeHeight(double value)
00797:         {
00798:             if (Math.Abs(value - 1.8) < 0.05) return 1.8;
00799:             if (Math.Abs(value - 5.0) < 0.05) return 5.0;
00800:             return 2.0;
00801:         }
00802: 
00803:         private static string FormatNullable(double? value, string suffix)
00804:         {
00805:             return value.HasValue
00806:                 ? value.Value.ToString("N3", CultureInfo.CurrentCulture) + suffix
00807:                 : string.Empty;
00808:         }
00809: 
00810:         private static string FormatPoint(Point3d point)
00811:         {
00812:             return string.Format(
00813:                 CultureInfo.CurrentCulture,
00814:                 "X {0:N3}; Y {1:N3}; Z {2:N3}",
00815:                 point.X,
00816:                 point.Y,
00817:                 point.Z);
00818:         }
00819: 
00820:         private static KeyValuePair<string, string> Pair(string key, string value)
00821:         {
00822:             return new KeyValuePair<string, string>(key, value);
00823:         }
00824: 
00825:         private static Document ActiveDocument()
00826:         {
00827:             return AcApplication.DocumentManager.MdiActiveDocument;
00828:         }
00829:     }
00830: 
00831:     internal sealed class RationalFlowScenario
00832:     {
00833:         public RationalFlowScenario(int returnPeriod, double intensity, double flow)
00834:         {
00835:             ReturnPeriod = returnPeriod;
00836:             Intensity = intensity;
00837:             Flow = flow;
00838:         }
00839: 
00840:         public int ReturnPeriod { get; private set; }
00841:         public double Intensity { get; private set; }
00842:         public double Flow { get; private set; }
00843:     }
```

## ModelDesignAuditCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 17-97
```csharp
00017:     /// <summary>
00018:     /// Drawing-wide Civil 3D model inventory and health audit. The report reads
00019:     /// current drawing state only: it does not rebuild, purge, repair or alter
00020:     /// design objects. Findings are prioritised and exported with corrective actions.
00021:     /// </summary>
00022:     public sealed class ModelDesignAuditCommands
00023:     {
00024:         private const string CePrefix = "CE_";
00025:         private static readonly string[] HandlePrefixes =
00026:         {
00027:             "Handle=", "Source=", "Boundary=", "Generated=", "Anchor="
00028:         };
00029: 
00030:         [CommandMethod("CE_TOOLS", "CE_MODELREPORTTOOLS", CommandFlags.Modal)]
00031:         public void ModelReportTools()
00032:         {
00033:             Document document = ActiveDocument();
00034:             if (document == null) return;
00035:             var options = new PromptKeywordOptions(
00036:                 "\nCivil 3D model report [Report/Summary/Export] <Report>: ")
00037:             {
00038:                 AllowNone = true
00039:             };
00040:             options.Keywords.Add("Report");
00041:             options.Keywords.Add("Summary");
00042:             options.Keywords.Add("Export");
00043:             PromptResult result = document.Editor.GetKeywords(options);
00044:             if (result.Status == PromptStatus.Cancel) return;
00045:             string choice = result.Status == PromptStatus.OK
00046:                 ? result.StringResult
00047:                 : "Report";
00048:             string command = Equal(choice, "Summary")
00049:                 ? "CE_MODELREPORTINFO "
00050:                 : Equal(choice, "Export")
00051:                     ? "CE_MODELREPORTEXPORT "
00052:                     : "CE_MODELREPORT ";
00053:             document.SendStringToExecute(command, true, false, true);
00054:         }
00055: 
00056:         [CommandMethod("CE_TOOLS", "CE_MODELREPORT", CommandFlags.Modal | CommandFlags.Redraw)]
00057:         public void ModelReport()
00058:         {
00059:             Document document = ActiveDocument();
00060:             if (document == null) return;
00061:             ModelAuditSnapshot snapshot = BuildSnapshot(document);
00062:             GridReportPresenter.ShowReportAndOfferTable(
00063:                 document,
00064:                 "CE Tools - Civil 3D Design Model Audit",
00065:                 BuildSubtitle(snapshot),
00066:                 BuildRows(snapshot, false),
00067:                 "CE TOOLS CIVIL 3D DESIGN MODEL AUDIT");
00068:             WriteCompletion(document.Editor, "CE_MODELREPORT", snapshot);
00069:         }
00070: 
00071:         [CommandMethod("CE_TOOLS", "CE_MODELREPORTINFO", CommandFlags.Modal | CommandFlags.Redraw)]
00072:         public void ModelReportInformation()
00073:         {
00074:             Document document = ActiveDocument();
00075:             if (document == null) return;
00076:             ModelAuditSnapshot snapshot = BuildSnapshot(document);
00077:             GridReportPresenter.ShowReportAndOfferTable(
00078:                 document,
00079:                 "CE Tools - Civil 3D Model Health Summary",
00080:                 BuildSubtitle(snapshot),
00081:                 BuildRows(snapshot, true),
00082:                 "CE TOOLS CIVIL 3D MODEL HEALTH SUMMARY");
00083:             WriteCompletion(document.Editor, "CE_MODELREPORTINFO", snapshot);
00084:         }
00085: 
00086:         [CommandMethod("CE_TOOLS", "CE_MODELREPORTEXPORT", CommandFlags.Modal | CommandFlags.Redraw)]
00087:         public void ExportModelReport()
00088:         {
00089:             Document document = ActiveDocument();
00090:             if (document == null) return;
00091:             ModelAuditSnapshot snapshot = BuildSnapshot(document);
00092:             string path;
00093:             if (!PromptExcelPath(
00094:                     document.Editor,
00095:                     "CE-Tools-Civil3D-Model-Audit.xlsx",
00096:                     out path))
00097:                 return;
```

## NetworkAssetScheduleCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 15-143
```csharp
00015: namespace CETools.Civil3D
00016: {
00017:     /// <summary>
00018:     /// Linked stormwater, sewer and pressure-network asset schedules. Values are
00019:     /// read from available Civil 3D properties by reflection so unsupported fields
00020:     /// remain blank rather than receiving invented values. Source handles can be
00021:     /// handed to the existing CE linked BOQ builder.
00022:     /// </summary>
00023:     public sealed class NetworkAssetScheduleCommands
00024:     {
00025:         private const string LinkRecordName = "CE_NETWORK_ASSET_SCHEDULE";
00026:         private const string SchemaVersion = "1";
00027: 
00028:         [CommandMethod("CE_TOOLS", "CE_NETWORKSCHEDULETOOLS", CommandFlags.Modal)]
00029:         public void NetworkScheduleTools()
00030:         {
00031:             Document document = ActiveDocument();
00032:             if (document == null) return;
00033:             var options = new PromptKeywordOptions(
00034:                 "\nNetwork schedule tools [Create/Refresh/Export/Info/BOQ] <Create>: ")
00035:             {
00036:                 AllowNone = true
00037:             };
00038:             options.Keywords.Add("Create");
00039:             options.Keywords.Add("Refresh");
00040:             options.Keywords.Add("Export");
00041:             options.Keywords.Add("Info");
00042:             options.Keywords.Add("BOQ");
00043:             PromptResult result = document.Editor.GetKeywords(options);
00044:             if (result.Status == PromptStatus.Cancel) return;
00045:             string choice = result.Status == PromptStatus.OK ? result.StringResult : "Create";
00046:             string command;
00047:             if (string.Equals(choice, "Refresh", StringComparison.OrdinalIgnoreCase))
00048:                 command = "CE_NETWORKSCHEDULEREFRESH ";
00049:             else if (string.Equals(choice, "Export", StringComparison.OrdinalIgnoreCase))
00050:                 command = "CE_NETWORKSCHEDULEEXPORT ";
00051:             else if (string.Equals(choice, "Info", StringComparison.OrdinalIgnoreCase))
00052:                 command = "CE_NETWORKSCHEDULEINFO ";
00053:             else if (string.Equals(choice, "BOQ", StringComparison.OrdinalIgnoreCase))
00054:                 command = "CE_NETWORKSCHEDULEBOQ ";
00055:             else
00056:                 command = "CE_NETWORKSCHEDULE ";
00057:             document.SendStringToExecute(command, true, false, true);
00058:         }
00059: 
00060:         [CommandMethod("CE_TOOLS", "CE_NETWORKSCHEDULE", CommandFlags.Modal | CommandFlags.Redraw)]
00061:         public void CreateNetworkSchedule()
00062:         {
00063:             Document document = ActiveDocument();
00064:             if (document == null) return;
00065:             Editor editor = document.Editor;
00066: 
00067:             var disciplineOptions = new PromptKeywordOptions(
00068:                 "\nNetwork asset scope [All/Stormwater/Sewer/Water] <All>: ")
00069:             {
00070:                 AllowNone = true
00071:             };
00072:             disciplineOptions.Keywords.Add("All");
00073:             disciplineOptions.Keywords.Add("Stormwater");
00074:             disciplineOptions.Keywords.Add("Sewer");
00075:             disciplineOptions.Keywords.Add("Water");
00076:             PromptResult disciplineResult = editor.GetKeywords(disciplineOptions);
00077:             if (disciplineResult.Status == PromptStatus.Cancel) return;
00078:             string scope = disciplineResult.Status == PromptStatus.OK
00079:                 ? disciplineResult.StringResult
00080:                 : "All";
00081: 
00082:             var sourceOptions = new PromptKeywordOptions(
00083:                 "\nAsset source [EntireDrawing/Select] <EntireDrawing>: ")
00084:             {
00085:                 AllowNone = true
00086:             };
00087:             sourceOptions.Keywords.Add("EntireDrawing");
00088:             sourceOptions.Keywords.Add("Select");
00089:             PromptResult sourceResult = editor.GetKeywords(sourceOptions);
00090:             if (sourceResult.Status == PromptStatus.Cancel) return;
00091:             bool selectedOnly = sourceResult.Status == PromptStatus.OK &&
00092:                 string.Equals(sourceResult.StringResult, "Select", StringComparison.OrdinalIgnoreCase);
00093: 
00094:             List<ObjectId> sourceIds;
00095:             if (selectedOnly)
00096:             {
00097:                 PromptSelectionResult selection = editor.GetSelection(new PromptSelectionOptions
00098:                 {
00099:                     MessageForAdding = "\nSelect network pipes, structures, fittings, bends and appurtenances: ",
00100:                     AllowDuplicates = false,
00101:                     RejectObjectsFromNonCurrentSpace = true
00102:                 });
00103:                 if (selection.Status != PromptStatus.OK) return;
00104:                 sourceIds = selection.Value.GetObjectIds().ToList();
00105:             }
00106:             else
00107:             {
00108:                 sourceIds = ReadAllDatabaseObjectIds(document.Database);
00109:             }
00110: 
00111:             var link = new NetworkScheduleLink(scope, sourceIds.Select(id => id.Handle.ToString()));
00112:             int rejected;
00113:             List<NetworkAssetRow> rows;
00114:             using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00115:             {
00116:                 rows = ReadRows(document.Database, transaction, link, out rejected);
00117:             }
00118:             if (rows.Count == 0)
00119:             {
00120:                 editor.WriteMessage(
00121:                     "\nCE_NETWORKSCHEDULE stopped. No supported network assets matched the selected scope.");
00122:                 return;
00123:             }
00124: 
00125:             PromptPointResult insertion = editor.GetPoint(
00126:                 "\nPick insertion point for the linked network asset schedule: ");
00127:             if (insertion.Status != PromptStatus.OK) return;
00128:             AnnotationOptions annotation;
00129:             if (!AnnotationSettingsStore.Prepare(document, false, out annotation)) return;
00130: 
00131:             var review = new List<KeyValuePair<string, string>>
00132:             {
00133:                 Pair("Scope", scope),
00134:                 Pair("Source mode", selectedOnly ? "Selected objects" : "Entire drawing"),
00135:                 Pair("Supported assets", rows.Count.ToString(CultureInfo.InvariantCulture)),
00136:                 Pair("Rejected/non-network objects", rejected.ToString(CultureInfo.InvariantCulture)),
00137:                 Pair("Columns", "Discipline, network, type, name, description, family, size, length, slope, bend angle, start/end levels"),
00138:                 Pair("Linked refresh", "Yes"),
00139:                 Pair("BOQ handoff", "Yes")
00140:             };
00141:             if (!PopupTablePresenter.ShowReview(
00142:                     "CE Tools - Network Asset Schedule",
00143:                     "Only values exposed by the current Civil 3D object are written. Missing fields remain blank and can be reviewed before issue.",
```

### Lines 686-765
```csharp
00686:                     if (table == null || table.ExtensionDictionary.IsNull) continue;
00687:                     DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
00688:                     if (dictionary != null && dictionary.Contains(LinkRecordName)) result.Add(id);
00689:                 }
00690:             }
00691:             return result;
00692:         }
00693: 
00694:         private static PromptEntityResult PromptTable(Editor editor, string message)
00695:         {
00696:             var options = new PromptEntityOptions(message);
00697:             options.SetRejectMessage("\nSelect an AutoCAD table.");
00698:             options.AddAllowedClass(typeof(Table), false);
00699:             return editor.GetEntity(options);
00700:         }
00701: 
00702:         private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
00703:         {
00704:             var options = new PromptKeywordOptions(
00705:                 "\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00706:             {
00707:                 AllowNone = true
00708:             };
00709:             options.Keywords.Add("Yes");
00710:             options.Keywords.Add("No");
00711:             PromptResult result = editor.GetKeywords(options);
00712:             if (result.Status == PromptStatus.Cancel) return false;
00713:             return result.Status == PromptStatus.None
00714:                 ? defaultValue
00715:                 : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00716:         }
00717: 
00718:         private static bool TryResolveHandle(Database database, string text, out ObjectId id)
00719:         {
00720:             id = ObjectId.Null;
00721:             long value;
00722:             if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
00723:             try
00724:             {
00725:                 id = database.GetObjectId(false, new Handle(value), 0);
00726:                 return !id.IsNull && !id.IsErased;
00727:             }
00728:             catch
00729:             {
00730:                 return false;
00731:             }
00732:         }
00733: 
00734:         private static string ReadString(object value, string propertyName)
00735:         {
00736:             object raw = ReadProperty(value, propertyName);
00737:             return Convert.ToString(raw, CultureInfo.CurrentCulture) ?? string.Empty;
00738:         }
00739: 
00740:         private static string ReadNestedString(object value, string parent, string child)
00741:         {
00742:             return ReadString(ReadProperty(value, parent), child);
00743:         }
00744: 
00745:         private static double? ReadDouble(object value, params string[] names)
00746:         {
00747:             foreach (string name in names)
00748:             {
00749:                 object raw = ReadProperty(value, name);
00750:                 if (raw == null) continue;
00751:                 try
00752:                 {
00753:                     double result = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
00754:                     if (!double.IsNaN(result) && !double.IsInfinity(result)) return result;
00755:                 }
00756:                 catch
00757:                 {
00758:                     // Try next property.
00759:                 }
00760:             }
00761:             return null;
00762:         }
00763: 
00764:         private static bool HasProperty(object value, string name)
00765:         {
```

## ParkingCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 370-448
```csharp
00370:             editor.WriteMessage(
00371:                 "\nCE_PKCOUNT complete. Parking bays counted={0}; skipped={1}; groups={2}.",
00372:                 total,
00373:                 skipped,
00374:                 groups.Count);
00375:         }
00376: 
00377:         private static void NumberParkingBays(Document document)
00378:         {
00379:             Editor editor = document.Editor;
00380:             PromptSelectionResult selection = GetSelection(
00381:                 editor,
00382:                 "\nSelect parking bay blocks and/or closed bay polylines to number: ");
00383:             if (selection.Status != PromptStatus.OK)
00384:             {
00385:                 return;
00386:             }
00387: 
00388:             var prefixOptions = new PromptStringOptions("\nEnter bay number prefix <P>: ")
00389:             {
00390:                 AllowSpaces = false,
00391:                 DefaultValue = "P",
00392:                 UseDefaultValue = true
00393:             };
00394:             PromptResult prefixResult = editor.GetString(prefixOptions);
00395:             if (prefixResult.Status != PromptStatus.OK)
00396:             {
00397:                 return;
00398:             }
00399: 
00400:             var startOptions = new PromptIntegerOptions("\nEnter starting number <1>: ")
00401:             {
00402:                 AllowNone = true,
00403:                 DefaultValue = 1,
00404:                 UseDefaultValue = true
00405:             };
00406:             PromptIntegerResult startResult = editor.GetInteger(startOptions);
00407:             if (startResult.Status != PromptStatus.OK)
00408:             {
00409:                 return;
00410:             }
00411: 
00412:             var incrementOptions = new PromptIntegerOptions("\nEnter numbering increment <1>: ")
00413:             {
00414:                 AllowNone = true,
00415:                 DefaultValue = 1,
00416:                 UseDefaultValue = true
00417:             };
00418:             PromptIntegerResult incrementResult = editor.GetInteger(incrementOptions);
00419:             if (incrementResult.Status != PromptStatus.OK)
00420:             {
00421:                 return;
00422:             }
00423: 
00424:             if (incrementResult.Value == 0)
00425:             {
00426:                 editor.WriteMessage("\nCE_PKNUMBER cancelled. Increment cannot be zero.");
00427:                 return;
00428:             }
00429: 
00430:             double defaultHeight = GetTextHeight(document.Database);
00431:             var heightOptions = new PromptDoubleOptions(
00432:                 string.Format(
00433:                     CultureInfo.CurrentCulture,
00434:                     "\nEnter bay number text height <{0:N3}>: ",
00435:                     defaultHeight))
00436:             {
00437:                 AllowNone = true,
00438:                 AllowNegative = false,
00439:                 AllowZero = false,
00440:                 DefaultValue = defaultHeight,
00441:                 UseDefaultValue = true
00442:             };
00443:             PromptDoubleResult heightResult = editor.GetDouble(heightOptions);
00444:             if (heightResult.Status != PromptStatus.OK)
00445:             {
00446:                 return;
00447:             }
00448: 
```

### Lines 675-800
```csharp
00675:             double aisleWidth = 0.0;
00676:             string side = "Left";
00677: 
00678:             if (includeAisle)
00679:             {
00680:                 PromptDoubleResult aisleResult = PromptPositiveDouble(
00681:                     editor,
00682:                     "\nEnter aisle width <6.000>: ",
00683:                     6.0);
00684:                 if (aisleResult.Status != PromptStatus.OK)
00685:                 {
00686:                     return null;
00687:                 }
00688: 
00689:                 aisleWidth = aisleResult.Value;
00690:             }
00691:             else
00692:             {
00693:                 var sideOptions = new PromptKeywordOptions(
00694:                     "\nCreate parking bays on which side [Left/Right] <Left>: ")
00695:                 {
00696:                     AllowNone = true
00697:                 };
00698:                 sideOptions.Keywords.Add("Left");
00699:                 sideOptions.Keywords.Add("Right");
00700:                 PromptResult sideResult = editor.GetKeywords(sideOptions);
00701:                 if (sideResult.Status == PromptStatus.Cancel)
00702:                 {
00703:                     return null;
00704:                 }
00705: 
00706:                 if (sideResult.Status == PromptStatus.OK)
00707:                 {
00708:                     side = sideResult.StringResult;
00709:                 }
00710:             }
00711: 
00712:             return new ParkingParameters(
00713:                 widthResult.Value,
00714:                 depthResult.Value,
00715:                 angleResult.Value,
00716:                 aisleWidth,
00717:                 side);
00718:         }
00719: 
00720:         private static PromptDoubleResult PromptPositiveDouble(
00721:             Editor editor,
00722:             string message,
00723:             double defaultValue)
00724:         {
00725:             return editor.GetDouble(
00726:                 new PromptDoubleOptions(message)
00727:                 {
00728:                     AllowNone = true,
00729:                     AllowNegative = false,
00730:                     AllowZero = false,
00731:                     DefaultValue = defaultValue,
00732:                     UseDefaultValue = true
00733:                 });
00734:         }
00735: 
00736:         private static bool ConfirmCreation(Editor editor, string message)
00737:         {
00738:             var options = new PromptKeywordOptions(
00739:                 "\n" + message + "? [Yes/No] <No>: ")
00740:             {
00741:                 AllowNone = true
00742:             };
00743:             options.Keywords.Add("Yes");
00744:             options.Keywords.Add("No");
00745: 
00746:             PromptResult result = editor.GetKeywords(options);
00747:             return result.Status == PromptStatus.OK &&
00748:                 string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00749:         }
00750: 
00751:         private static int CalculateBayCount(double baselineLength, double bayWidth)
00752:         {
00753:             return (int)Math.Floor((baselineLength + GeometryTolerance) / bayWidth);
00754:         }
00755: 
00756:         private static double DegreesToRadians(double degrees)
00757:         {
00758:             return degrees * Math.PI / 180.0;
00759:         }
00760: 
00761:         private static void AppendClosedBay(
00762:             BlockTableRecord currentSpace,
00763:             Transaction transaction,
00764:             ObjectId layerId,
00765:             Point3d frontStart,
00766:             Point3d frontEnd,
00767:             Point3d backEnd,
00768:             Point3d backStart)
00769:         {
00770:             var bay = new Polyline(4);
00771:             bay.SetDatabaseDefaults();
00772:             bay.LayerId = layerId;
00773:             bay.Elevation = frontStart.Z;
00774:             bay.AddVertexAt(0, new Point2d(frontStart.X, frontStart.Y), 0.0, 0.0, 0.0);
00775:             bay.AddVertexAt(1, new Point2d(frontEnd.X, frontEnd.Y), 0.0, 0.0, 0.0);
00776:             bay.AddVertexAt(2, new Point2d(backEnd.X, backEnd.Y), 0.0, 0.0, 0.0);
00777:             bay.AddVertexAt(3, new Point2d(backStart.X, backStart.Y), 0.0, 0.0, 0.0);
00778:             bay.Closed = true;
00779:             currentSpace.AppendEntity(bay);
00780:             transaction.AddNewlyCreatedDBObject(bay, true);
00781:         }
00782: 
00783:         private static BlockTableRecord OpenCurrentSpace(
00784:             Database database,
00785:             Transaction transaction)
00786:         {
00787:             return (BlockTableRecord)transaction.GetObject(
00788:                 database.CurrentSpaceId,
00789:                 OpenMode.ForWrite,
00790:                 false);
00791:         }
00792: 
00793:         private static PromptSelectionResult GetSelection(Editor editor, string message)
00794:         {
00795:             PromptSelectionResult implied = editor.SelectImplied();
00796:             if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
00797:             {
00798:                 editor.SetImpliedSelection(new ObjectId[0]);
00799:                 return implied;
00800:             }
```

## ParkingDynamicGradingCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 80-160
```csharp
00080:                 new List<DisciplineWorkflowAction>
00081:                 {
00082:                     new DisciplineWorkflowAction("Create grading guides", "CE_PARKGRADECREATE", "Create linked 3D grading guides for a parking option.", "01 Grading"),
00083:                     new DisciplineWorkflowAction("Refresh grading guides", "CE_PARKGRADEREFRESH", "Rebuild grading guides after source changes.", "01 Grading"),
00084:                     new DisciplineWorkflowAction("Grading information", "CE_PARKGRADEINFO", "Inspect grading linkage and current settings.", "02 Review"),
00085:                     new DisciplineWorkflowAction("Clear grading guides", "CE_PARKGRADECLEAR", "Remove generated parking grading guides.", "03 Cleanup")
00086:                 });
00087:         }
00088: 
00089:         [CommandMethod("CE_TOOLS", "CE_PARKGRADECREATE", CommandFlags.Modal | CommandFlags.Redraw)]
00090:         public void CreateParkingGradeGuide()
00091:         {
00092:             Document document = ActiveDocument();
00093:             if (document == null) return;
00094:             Editor editor = document.Editor;
00095:             ParkingGradeBoundary boundary = ParkingGradeGuideStore.PromptBoundary(document);
00096:             if (boundary == null) return;
00097: 
00098:             var modeOptions = new PromptKeywordOptions(
00099:                 "\nParking grading mode [LowPoint/Crown/Valley] <LowPoint>: ")
00100:             {
00101:                 AllowNone = true
00102:             };
00103:             modeOptions.Keywords.Add("LowPoint");
00104:             modeOptions.Keywords.Add("Crown");
00105:             modeOptions.Keywords.Add("Valley");
00106:             PromptResult modeResult = editor.GetKeywords(modeOptions);
00107:             if (modeResult.Status == PromptStatus.Cancel) return;
00108:             ParkingGradeMode mode = ParseMode(
00109:                 modeResult.Status == PromptStatus.OK ? modeResult.StringResult : "LowPoint");
00110: 
00111:             double slope;
00112:             double referenceElevation;
00113:             double spacing;
00114:             if (!PromptPositiveDouble(editor, "Design slope (%)", 2.0, out slope)) return;
00115:             if (!PromptAnyDouble(editor, "Reference elevation", boundary.Elevation, out referenceElevation)) return;
00116:             if (!PromptPositiveDouble(
00117:                     editor,
00118:                     "Guide spacing in drawing units",
00119:                     Math.Max(boundary.Length / 10.0, 1.0),
00120:                     out spacing))
00121:                 return;
00122: 
00123:             Point3d lowPoint = boundary.CentreWorld;
00124:             if (mode == ParkingGradeMode.LowPoint)
00125:             {
00126:                 PromptPointResult pointResult = editor.GetPoint(
00127:                     "\nPick the intended parking low point inside the boundary: ");
00128:                 if (pointResult.Status != PromptStatus.OK) return;
00129:                 Point2d local = boundary.ToLocal(pointResult.Value);
00130:                 if (!ParkingGradeGuideStore.PointInPolygon(boundary.Polygon, local))
00131:                 {
00132:                     editor.WriteMessage(
00133:                         "\nCE_PARKGRADECREATE stopped. The selected low point lies outside the parking boundary.");
00134:                     return;
00135:                 }
00136:                 lowPoint = new Point3d(
00137:                     pointResult.Value.X,
00138:                     pointResult.Value.Y,
00139:                     referenceElevation);
00140:             }
00141: 
00142:             var settings = new ParkingGradeSettings(
00143:                 mode,
00144:                 slope,
00145:                 referenceElevation,
00146:                 spacing,
00147:                 lowPoint);
00148:             List<IList<Point3d>> guides = ParkingGradeGuideStore.BuildGuides(boundary, settings);
00149:             if (guides.Count == 0)
00150:             {
00151:                 editor.WriteMessage(
00152:                     "\nCE_PARKGRADECREATE stopped. No grading guide geometry could be generated from this boundary.");
00153:                 return;
00154:             }
00155: 
00156:             var review = new List<KeyValuePair<string, string>>
00157:             {
00158:                 Pair("Mode", settings.Mode.ToString()),
00159:                 Pair("Slope", settings.SlopePercent.ToString("N3", CultureInfo.CurrentCulture) + "%"),
00160:                 Pair("Reference elevation", settings.ReferenceElevation.ToString("N3", CultureInfo.CurrentCulture)),
```

## ParkingOptimiserCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 19-98
```csharp
00019:     /// <summary>
00020:     /// Obstacle-aware parking concept optimiser. Generated alternatives remain
00021:     /// drafting/design assistance and require review against governing parking,
00022:     /// accessibility, traffic, fire, drainage and swept-path standards.
00023:     /// </summary>
00024:     public sealed class ParkingOptimiserCommands
00025:     {
00026:         private const string RegAppName = "CE_PARK_OPTIMISER";
00027:         private const string SchemaVersion = "1";
00028:         private const string LayerPrefix = "CE-PARK-OPT-";
00029:         private const int MaximumObstacles = 500;
00030:         private const double Tolerance = 1e-8;
00031: 
00032:         [CommandMethod("CE_TOOLS", "CE_PARKOPTIMIZERTOOLS", CommandFlags.Modal)]
00033:         public void ParkingOptimiserTools()
00034:         {
00035:             Document document = ActiveDocument();
00036:             if (document == null) return;
00037:             var options = new PromptKeywordOptions(
00038:                 "\nFull parking optimiser [Create/Refresh/Info/Export/Clear] <Create>: ")
00039:             {
00040:                 AllowNone = true
00041:             };
00042:             foreach (string keyword in new[] { "Create", "Refresh", "Info", "Export", "Clear" })
00043:                 options.Keywords.Add(keyword);
00044:             PromptResult result = document.Editor.GetKeywords(options);
00045:             if (result.Status == PromptStatus.Cancel) return;
00046:             string choice = result.Status == PromptStatus.OK ? result.StringResult : "Create";
00047:             string command = Equal(choice, "Refresh") ? "CE_PARKOPTREFRESH " :
00048:                 Equal(choice, "Info") ? "CE_PARKOPTINFO " :
00049:                 Equal(choice, "Export") ? "CE_PARKOPTEXPORT " :
00050:                 Equal(choice, "Clear") ? "CE_PARKOPTCLEAR " : "CE_PARKOPTIMIZE ";
00051:             document.SendStringToExecute(command, true, false, true);
00052:         }
00053: 
00054:         [CommandMethod("CE_TOOLS", "CE_PARKOPTIMIZE", CommandFlags.Modal | CommandFlags.Redraw)]
00055:         public void OptimiseParking()
00056:         {
00057:             Document document = ActiveDocument();
00058:             if (document == null) return;
00059:             ParkingOptimiserInput input;
00060:             if (!PromptNewInput(document, out input)) return;
00061: 
00062:             try
00063:             {
00064:                 IReadOnlyList<ParkingLayoutOption> options = RunOptimiser(input);
00065:                 ShowOptions(document, options, input.Settings.TargetBayCount);
00066:                 int selectedIndex;
00067:                 if (!PromptOptionIndex(document.Editor, options.Count, out selectedIndex)) return;
00068:                 ParkingLayoutOption selected = options[selectedIndex];
00069:                 if (!ConfirmOption(document.Editor, selected)) return;
00070:                 int created = ReplaceLinkedLayout(document.Database, input, selected);
00071:                 document.Editor.Regen();
00072:                 document.Editor.WriteMessage(
00073:                     "\nCE_PARKOPTIMIZE complete. Option={0}; angle={1:N0}; orientation={2:N1}; standard={3}; accessible={4}; islands={5}; graphics={6}.",
00074:                     selectedIndex + 1,
00075:                     selected.ParkingAngleDegrees,
00076:                     selected.OrientationDegrees,
00077:                     selected.StandardBayCount,
00078:                     selected.AccessibleBayCount,
00079:                     selected.Islands.Count,
00080:                     created);
00081:             }
00082:             catch (System.Exception exception)
00083:             {
00084:                 document.Editor.WriteMessage(
00085:                     "\nCE_PARKOPTIMIZE failed. No optimiser transaction was committed. {0}",
00086:                     exception.Message);
00087:             }
00088:         }
00089: 
00090:         [CommandMethod("CE_TOOLS", "CE_PARKOPTREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
00091:         public void RefreshOptimisedParking()
00092:         {
00093:             Document document = ActiveDocument();
00094:             if (document == null) return;
00095:             ParkingOptimiserLink link;
00096:             if (!PromptLinkedSet(document, out link)) return;
00097:             ParkingOptimiserInput input;
00098:             if (!RebuildInput(document.Database, link, out input))
```

### Lines 779-853
```csharp
00779:             PromptDoubleResult result = editor.GetDouble(options);
00780:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00781:             return result.Status != PromptStatus.Cancel && value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
00782:         }
00783: 
00784:         private static bool PromptNonNegativeDouble(Editor editor, string label, double defaultValue, out double value)
00785:         {
00786:             var options = new PromptDoubleOptions("\n" + label + " <" + defaultValue.ToString(CultureInfo.CurrentCulture) + ">: ")
00787:             {
00788:                 AllowNone = true, AllowNegative = false, AllowZero = true, DefaultValue = defaultValue
00789:             };
00790:             PromptDoubleResult result = editor.GetDouble(options);
00791:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00792:             return result.Status != PromptStatus.Cancel && value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
00793:         }
00794: 
00795:         private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
00796:         {
00797:             var options = new PromptKeywordOptions("\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ") { AllowNone = true };
00798:             options.Keywords.Add("Yes"); options.Keywords.Add("No");
00799:             PromptResult result = editor.GetKeywords(options);
00800:             if (result.Status == PromptStatus.Cancel) return false;
00801:             return result.Status == PromptStatus.None ? defaultValue : Equal(result.StringResult, "Yes");
00802:         }
00803: 
00804:         private static IList<string> Row(string property, string value)
00805:         {
00806:             return new List<string> { property, value };
00807:         }
00808: 
00809:         private static bool Equal(string first, string second)
00810:         {
00811:             return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
00812:         }
00813: 
00814:         private static Document ActiveDocument()
00815:         {
00816:             return AcApplication.DocumentManager.MdiActiveDocument;
00817:         }
00818:     }
00819: 
00820:     internal sealed class ParkingOptimiserInput
00821:     {
00822:         public ParkingOptimiserInput(
00823:             ObjectId boundaryId,
00824:             string boundaryHandle,
00825:             ParkingPolygon boundary,
00826:             double elevation,
00827:             ObjectId[] obstacleIds,
00828:             List<string> obstacleHandles,
00829:             List<ParkingPolygon> obstacles,
00830:             ParkingPoint entrance,
00831:             ParkingLayoutSettings settings)
00832:         {
00833:             BoundaryId = boundaryId; BoundaryHandle = boundaryHandle; Boundary = boundary;
00834:             Elevation = elevation; ObstacleIds = obstacleIds; ObstacleHandles = obstacleHandles;
00835:             Obstacles = obstacles; Entrance = entrance; Settings = settings;
00836:         }
00837:         public ObjectId BoundaryId { get; private set; }
00838:         public string BoundaryHandle { get; private set; }
00839:         public ParkingPolygon Boundary { get; private set; }
00840:         public double Elevation { get; private set; }
00841:         public ObjectId[] ObstacleIds { get; private set; }
00842:         public List<string> ObstacleHandles { get; private set; }
00843:         public List<ParkingPolygon> Obstacles { get; private set; }
00844:         public ParkingPoint Entrance { get; private set; }
00845:         public ParkingLayoutSettings Settings { get; private set; }
00846:     }
00847: 
00848:     internal sealed class ParkingOptimiserLink
00849:     {
00850:         public string BoundaryHandle { get; private set; }
00851:         public List<string> ObstacleHandles { get; private set; }
00852:         public string ElementType { get; private set; }
00853:         public string ElementName { get; private set; }
```

## ParkingSkewValidationCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 24-106
```csharp
00024:     {
00025:         private const string RegAppName = "CE_TOOLS_PK_SKEW";
00026:         private const string SettingsDictionary = "CE_TOOLS";
00027:         private const string SettingsRecord = "PARKING_SKEW_SETTINGS";
00028:         private const string DefaultReviewLayer = "CE-PARKING-WIDTH-REVIEW";
00029:         private const string DefaultCorrectionLayer = "CE-PARKING-WIDTH-CORRECTION";
00030:         private const double GeometryTolerance = 1e-8;
00031:         private const short PassColour = 3;
00032:         private const short FailColour = 1;
00033:         private const short CorrectionColour = 2;
00034: 
00035:         [CommandMethod("CE_PKSKTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
00036:         public void ParkingSkewTools()
00037:         {
00038:             Document document = ActiveDocument();
00039:             if (document == null)
00040:                 return;
00041: 
00042:             var options = new PromptKeywordOptions(
00043:                 "\nParking skew tools [Validate/Correct/Clear/Settings/Information] <Validate>: ")
00044:             {
00045:                 AllowNone = true
00046:             };
00047:             foreach (string keyword in new[]
00048:             {
00049:                 "Validate", "Correct", "Clear", "Settings", "Information"
00050:             })
00051:                 options.Keywords.Add(keyword);
00052:             PromptResult result = document.Editor.GetKeywords(options);
00053:             if (result.Status == PromptStatus.Cancel)
00054:                 return;
00055: 
00056:             string choice = result.Status == PromptStatus.OK
00057:                 ? result.StringResult
00058:                 : "Validate";
00059:             if (choice.Equals("Correct", StringComparison.OrdinalIgnoreCase))
00060:                 CorrectFailedBays();
00061:             else if (choice.Equals("Clear", StringComparison.OrdinalIgnoreCase))
00062:                 ClearReviewGraphics();
00063:             else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
00064:                 ConfigureSettings();
00065:             else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase))
00066:                 Information();
00067:             else
00068:                 ValidateBays();
00069:         }
00070: 
00071:         [CommandMethod("CE_PKSKSETTINGS", CommandFlags.Modal)]
00072:         public void ConfigureSettings()
00073:         {
00074:             Document document = ActiveDocument();
00075:             if (document == null)
00076:                 return;
00077: 
00078:             Editor editor = document.Editor;
00079:             ParkingSkewSettings settings = ParkingSkewSettings.Read(document.Database);
00080:             if (!PromptPositiveDouble(
00081:                     editor,
00082:                     "Required perpendicular bay width in millimetres",
00083:                     settings.RequiredWidthMillimetres,
00084:                     out settings.RequiredWidthMillimetres))
00085:                 return;
00086:             if (!PromptPositiveDouble(
00087:                     editor,
00088:                     "Drawing units per millimetre (1 for mm, 0.001 for metres)",
00089:                     settings.DrawingUnitsPerMillimetre,
00090:                     out settings.DrawingUnitsPerMillimetre))
00091:                 return;
00092:             if (!PromptNonNegativeDouble(
00093:                     editor,
00094:                     "Compliance tolerance in millimetres",
00095:                     settings.ToleranceMillimetres,
00096:                     out settings.ToleranceMillimetres))
00097:                 return;
00098:             if (!PromptPositiveDouble(
00099:                     editor,
00100:                     "Dimension/label text height in drawing units",
00101:                     settings.TextHeight,
00102:                     out settings.TextHeight))
00103:                 return;
00104:             if (!PromptPositiveDouble(
00105:                     editor,
00106:                     "Dimension offset in drawing units",
```

### Lines 408-487
```csharp
00408:                     "\nCE_PKSKCORRECT complete. Correction outlines created/refreshed={0}; compliant source bays changed=0; failed source bays changed=0.",
00409:                     created);
00410:             }
00411:             catch (System.Exception exception)
00412:             {
00413:                 document.Editor.WriteMessage(
00414:                     "\nCE_PKSKCORRECT failed. No correction transaction was committed. " +
00415:                     exception.Message);
00416:             }
00417:         }
00418: 
00419:         [CommandMethod("CE_PKSKCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
00420:         public void ClearReviewGraphics()
00421:         {
00422:             Document document = ActiveDocument();
00423:             if (document == null)
00424:                 return;
00425: 
00426:             var options = new PromptKeywordOptions(
00427:                 "\nClear parking skew graphics [SelectedSources/All] <SelectedSources>: ")
00428:             {
00429:                 AllowNone = true
00430:             };
00431:             options.Keywords.Add("SelectedSources");
00432:             options.Keywords.Add("All");
00433:             PromptResult result = document.Editor.GetKeywords(options);
00434:             if (result.Status == PromptStatus.Cancel)
00435:                 return;
00436:             bool clearAll = result.Status == PromptStatus.OK &&
00437:                 result.StringResult.Equals("All", StringComparison.OrdinalIgnoreCase);
00438: 
00439:             var sourceHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
00440:             if (!clearAll)
00441:             {
00442:                 PromptSelectionResult selection = GetParkingSelection(
00443:                     document.Editor,
00444:                     "\nSelect source bays whose CE skew graphics must be cleared: ");
00445:                 if (selection.Status != PromptStatus.OK)
00446:                     return;
00447:                 foreach (ObjectId id in selection.Value.GetObjectIds())
00448:                     sourceHandles.Add(id.Handle.ToString());
00449:             }
00450: 
00451:             if (!Confirm(document.Editor, clearAll
00452:                     ? "Erase all CE parking skew dimensions, labels and correction outlines in the current space"
00453:                     : "Erase CE parking skew graphics linked to the selected source bays"))
00454:                 return;
00455: 
00456:             try
00457:             {
00458:                 int erased = 0;
00459:                 using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00460:                 {
00461:                     BlockTableRecord currentSpace = transaction.GetObject(
00462:                         document.Database.CurrentSpaceId,
00463:                         OpenMode.ForRead,
00464:                         false) as BlockTableRecord;
00465:                     foreach (ObjectId id in currentSpace)
00466:                     {
00467:                         Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
00468:                         string type;
00469:                         string source;
00470:                         double measured;
00471:                         double required;
00472:                         if (!TryReadTag(entity, out type, out source, out measured, out required))
00473:                             continue;
00474:                         if (!clearAll && !sourceHandles.Contains(source))
00475:                             continue;
00476:                         entity.UpgradeOpen();
00477:                         entity.Erase();
00478:                         erased++;
00479:                     }
00480:                     transaction.Commit();
00481:                 }
00482:                 document.Editor.WriteMessage(
00483:                     "\nCE_PKSKCLEAR complete. Generated parking skew objects erased={0}. Source bays were not changed.",
00484:                     erased);
00485:             }
00486:             catch (System.Exception exception)
00487:             {
```

### Lines 1100-1236
```csharp
01100:             catch
01101:             {
01102:                 return false;
01103:             }
01104:         }
01105: 
01106:         private static void AddDistinct(ICollection<Point2d> points, Point2d point)
01107:         {
01108:             if (!points.Any(existing => existing.GetDistanceTo(point) <= GeometryTolerance))
01109:                 points.Add(point);
01110:         }
01111: 
01112:         private static bool PromptText(
01113:             Editor editor,
01114:             string label,
01115:             string current,
01116:             out string value)
01117:         {
01118:             var options = new PromptStringOptions(
01119:                 "\n" + label + " <" + (current ?? string.Empty) + ">: ")
01120:             {
01121:                 AllowSpaces = true
01122:             };
01123:             PromptResult result = editor.GetString(options);
01124:             if (result.Status == PromptStatus.Cancel)
01125:             {
01126:                 value = current;
01127:                 return false;
01128:             }
01129:             value = result.Status == PromptStatus.None
01130:                 ? current
01131:                 : result.StringResult.Trim();
01132:             return true;
01133:         }
01134: 
01135:         private static bool PromptPositiveDouble(
01136:             Editor editor,
01137:             string label,
01138:             double current,
01139:             out double value)
01140:         {
01141:             var options = new PromptDoubleOptions(
01142:                 "\n" + label + " <" + current.ToString("0.######", CultureInfo.InvariantCulture) + ">: ")
01143:             {
01144:                 AllowNegative = false,
01145:                 AllowZero = false,
01146:                 UseDefaultValue = true,
01147:                 DefaultValue = current
01148:             };
01149:             PromptDoubleResult result = editor.GetDouble(options);
01150:             value = result.Status == PromptStatus.OK ? result.Value : current;
01151:             return result.Status == PromptStatus.OK;
01152:         }
01153: 
01154:         private static bool PromptNonNegativeDouble(
01155:             Editor editor,
01156:             string label,
01157:             double current,
01158:             out double value)
01159:         {
01160:             var options = new PromptDoubleOptions(
01161:                 "\n" + label + " <" + current.ToString("0.######", CultureInfo.InvariantCulture) + ">: ")
01162:             {
01163:                 AllowNegative = false,
01164:                 AllowZero = true,
01165:                 UseDefaultValue = true,
01166:                 DefaultValue = current
01167:             };
01168:             PromptDoubleResult result = editor.GetDouble(options);
01169:             value = result.Status == PromptStatus.OK ? result.Value : current;
01170:             return result.Status == PromptStatus.OK;
01171:         }
01172: 
01173:         private static bool Confirm(Editor editor, string message)
01174:         {
01175:             var options = new PromptKeywordOptions(
01176:                 "\n" + message + "? [Yes/No] <No>: ")
01177:             {
01178:                 AllowNone = true
01179:             };
01180:             options.Keywords.Add("Yes");
01181:             options.Keywords.Add("No");
01182:             PromptResult result = editor.GetKeywords(options);
01183:             return result.Status == PromptStatus.OK &&
01184:                    result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
01185:         }
01186: 
01187:         private static Document ActiveDocument()
01188:         {
01189:             return AcApplication.DocumentManager.MdiActiveDocument;
01190:         }
01191: 
01192:         private sealed class ParkingAnalysis
01193:         {
01194:             public ParkingAnalysis()
01195:             {
01196:                 Candidates = new List<ParkingBayCandidate>();
01197:                 Rejections = new List<Rejection>();
01198:             }
01199:             public List<ParkingBayCandidate> Candidates { get; }
01200:             public List<Rejection> Rejections { get; }
01201:             public void Reject(ObjectId id, string reason)
01202:             {
01203:                 Rejections.Add(new Rejection(
01204:                     id.IsNull ? "<Invalid>" : id.Handle.ToString(),
01205:                     reason));
01206:             }
01207:         }
01208: 
01209:         private sealed class Rejection
01210:         {
01211:             public Rejection(string handle, string reason)
01212:             {
01213:                 Handle = handle;
01214:                 Reason = reason;
01215:             }
01216:             public string Handle { get; }
01217:             public string Reason { get; }
01218:         }
01219: 
01220:         private sealed class ParkingBayCandidate
01221:         {
01222:             public ParkingBayCandidate(
01223:                 ObjectId sourceId,
01224:                 string sourceHandle,
01225:                 string sourceType,
01226:                 Point3d center,
01227:                 Point2d longAxis,
01228:                 Vector2d shortAxis,
01229:                 double lengthDrawingUnits,
01230:                 double widthDrawingUnits,
01231:                 double lengthMillimetres,
01232:                 double widthMillimetres,
01233:                 double shortestEdgeMillimetres,
01234:                 double differenceMillimetres,
01235:                 double skewAngleDegrees,
01236:                 bool isCompliant)
```

## PhaseOneUtilityCommands.cs
Hits: `CE_BOOKINDEX`, `CE_CLIENTBOOK`

### Lines 173-246
```csharp
00173:                 document.SendStringToExecute("_.LAYER ", true, false, true);
00174:         }
00175: 
00176:         [CommandMethod("CE_TOOLS", "CE_EXCELTOOLS", CommandFlags.Modal)]
00177:         public void ExcelTools()
00178:         {
00179:             Document document = ActiveDocument();
00180:             if (document == null) return;
00181:             DisciplineWorkflowDialogs.SelectAndRun(
00182:                 document,
00183:                 "CE Tools - Excel Tools",
00184:                 "Open dependency-free Excel exports linked to current drawing data.",
00185:                 new List<DisciplineWorkflowAction>
00186:                 {
00187:                     Action("Export linked BOQ", "CE_BOQEXPORT", "Refresh and export a linked bill of quantities.", "01 Quantities"),
00188:                     Action("Export setting-out schedule", "CE_SETTINGOUTEXPORT", "Export linked COGO/AutoCAD point coordinates and levels.", "02 Survey"),
00189:                     Action("Export survey changes", "CE_SURVEYCHANGEEXPORT", "Export original-versus-corrected surface comparison results.", "02 Survey"),
00190:                     Action("Export project report", "CE_REPORTEXPORT", "Export a current model-derived engineering report.", "03 Reports"),
00191:                     Action("Export drawing-book index", "CE_BOOKINDEX", "Export the standard layout and drawing-book register.", "03 Reports"),
00192:                     Action("Export client-book index", "CE_CLIENTBOOKINDEX", "Export the linked client drawing-book register.", "03 Reports")
00193:                 });
00194:         }
00195: 
00196:         [CommandMethod("CE_TOOLS", "CE_LABELTOOLS", CommandFlags.Modal)]
00197:         public void LabelTools()
00198:         {
00199:             Document document = ActiveDocument();
00200:             if (document == null) return;
00201:             DisciplineWorkflowDialogs.SelectAndRun(
00202:                 document,
00203:                 "CE Tools - Label Utilities",
00204:                 "Create drawing-linked annotations using shared paper heights, output types and overlap controls.",
00205:                 new List<DisciplineWorkflowAction>
00206:                 {
00207:                     Action("Annotation settings", "CE_ANNOTSETTINGS", "Set 1.8/2.0/2.5/3.5/5.0 mm paper height, marker and output.", "01 Settings"),
00208:                     Action("Coordinate label", "CE_COORDPICK2", "Create a linked XYZ coordinate annotation.", "02 Survey"),
00209:                     Action("Coordinate cross", "CE_COORDCROSS2", "Create a linked coordinate cross and optional register entry.", "02 Survey"),
00210:                     Action("Alignment label", "CE_ALLABELX", "Create a station/offset annotation.", "03 Civil Objects"),
00211:                     Action("Profile label", "CE_PRLABELX", "Create a station/elevation/grade annotation.", "03 Civil Objects"),
00212:                     Action("Surface label", "CE_SFLABELX", "Create a surface elevation annotation.", "03 Civil Objects"),
00213:                     Action("Feature-line label", "CE_FLLABELX", "Create a feature-line elevation/grade annotation.", "03 Civil Objects"),
00214:                     Action("Corridor label", "CE_CORLABELX", "Create a corridor annotation.", "03 Civil Objects"),
00215:                     Action("Parking numbering", "CE_PKNUMBERX", "Create linked parking-bay numbering annotations.", "04 Parking"),
00216:                     Action("Resolve overlaps", "CE_OVERLAPFIX", "Reposition supported annotations to reduce collisions.", "05 Cleanup")
00217:                 });
00218:         }
00219: 
00220:         [CommandMethod("CE_TOOLS", "CE_SURVEYCLEANUP", CommandFlags.Modal)]
00221:         public void SurveyCleanup()
00222:         {
00223:             Document document = ActiveDocument();
00224:             if (document == null) return;
00225:             DisciplineWorkflowDialogs.SelectAndRun(
00226:                 document,
00227:                 "CE Tools - Survey Cleanup",
00228:                 "Audit and compare survey data while preserving original source geometry and surfaces.",
00229:                 new List<DisciplineWorkflowAction>
00230:                 {
00231:                     Action("Survey correction comparison", "CE_SURVEYCOMPARETOOLS", "Compare original and corrected survey surfaces.", "01 Compare"),
00232:                     Action("Surface correction tools", "CE_SURFCTOOLS", "Audit and create reversible corrected/simplified surface copies.", "02 Correct"),
00233:                     Action("Spike and hole repair", "CE_SURFSPIKEHOLEFIX", "Create a repaired copy while keeping the original surface.", "02 Correct"),
00234:                     Action("Coordinate utilities", "CE_COORDINATE", "Review or recreate coordinate labels, crosses and tables.", "03 Coordinates"),
00235:                     Action("Drawing cleanup", "CE_DRAWCLEAN", "Run controlled drawing cleanup after survey review.", "04 Drawing")
00236:                 });
00237:         }
00238: 
00239:         private static DisciplineWorkflowAction Action(
00240:             string title,
00241:             string command,
00242:             string description,
00243:             string group)
00244:         {
00245:             return new DisciplineWorkflowAction(title, command, description, group);
00246:         }
```

## PluginEntry.cs
Hits: `CE_DRAWINGBOOK`, `CE_BOOKINDEX`, `CE_CLIENTBOOK`, `CE_PROJECTCLOSEOUT`, `CE_PROJECTSETUP`

### Lines 189-261
```csharp
00189:                         Cmd("Create Road Corridors", "CE_ROADCORRIDORS ", "Create one corridor for each CE road alignment/profile pair."),
00190:                         Cmd("Road Production Information", "CE_ROADPRODUCTIONINFO ", "Review road alignments, profiles, corridors and styles."),
00191:                         Cmd("Road BOQ", "CE_BOQROAD ", "Create the road bill of quantities."),
00192:                         Cmd("Road Design Report", "CE_REPORTROAD ", "Generate the road design report."))));
00193:         }
00194: 
00195:         private static void AddProjectPanel(RibbonTab tab)
00196:         {
00197:             AddPanel(
00198:                 tab,
00199:                 ProjectPanelId,
00200:                 "Project",
00201:                 Row(
00202:                     Menu(
00203:                         "CE_TOOLS_PROJECT_MENU",
00204:                         "Project\nSetup",
00205:                         "Create, review, clear and restore portable project information.",
00206:                         Cmd("Phase 1 Utilities", "CE_PHASE1 ", "Open every original CE Tools Phase 1 utility family in one visual hub."),
00207:                         Cmd("Project Setup", "CE_PROJECTSETUP ", "Create or update project metadata and review it in a pop-up."),
00208:                         Cmd("Project Information", "CE_PROJECTINFO ", "Review project metadata and optionally place a drawing table."),
00209:                         Cmd("Clear Project Information", "CE_PROJECTCLEAR ", "Clear project metadata after confirmation and keep a recoverable backup."),
00210:                         Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear.")),
00211:                     Menu(
00212:                         "CE_TOOLS_COORDSYS_MENU",
00213:                         "Coordinate\nSystems",
00214:                         "Report, search, assign and clear the drawing coordinate system.",
00215:                         Cmd("Coordinate System Tools", "CE_COORDSYS ", "Open the coordinate-system menu."),
00216:                         Cmd("Information", "CE_COORDSYSINFO ", "Report the current coordinate system."),
00217:                         Cmd("Assign", "CE_COORDSYSASSIGN ", "Open Autodesk's native coordinate-system selection window."),
00218:                         Cmd("Assign by Code", "CE_COORDSYSCODE ", "Advanced direct assignment using a validated Autodesk code."),
00219:                         Cmd("Search Library", "CE_COORDSYSSEARCH ", "Search the installed coordinate-system library."),
00220:                         Cmd("Clear", "CE_COORDSYSCLEAR ", "Clear the assignment after confirmation.")),
00221:                     Menu(
00222:                         "CE_TOOLS_STANDARDS_MENU",
00223:                         "Project\nStandards",
00224:                         "Select a standards source file and record its project information.",
00225:                         Cmd("Standards Tools", "CE_STANDARDS ", "Open the standards menu."),
00226:                         Cmd("Select Standards", "CE_STANDARDSELECT ", "Browse for a standards file, review it and save its traceable details."),
00227:                         Cmd("Standards Information", "CE_STANDARDINFO ", "Review stored standards and optionally place a drawing table."),
00228:                         Cmd("Clear Standards", "CE_STANDARDCLEAR ", "Clear the standards record.")),
00229:                     Menu(
00230:                         "CE_TOOLS_PROJECT_STYLES_MENU",
00231:                         "Project\nStyles",
00232:                         "Select and review project Civil 3D styles by discipline.",
00233:                         Cmd("Import Source Styles", "CE_PROJECTSTYLEIMPORT ", "Import Civil 3D styles from the three supplied source drawings or another DWG/DWT."),
00234:                         Cmd("Project Style Centre", "CE_PROJECTSTYLES ", "Select alignment, profile, corridor, point and network styles."),
00235:                         Cmd("Project Style Information", "CE_PROJECTSTYLEINFO ", "Review stored project style selections."),
00236:                         Cmd("Clear Project Styles", "CE_PROJECTSTYLECLEAR ", "Clear only the stored project style selections.")),
00237:                     Menu(
00238:                         "CE_TOOLS_UNDO_MENU",
00239:                         "Undo &\nRedo",
00240:                         "Enable full native undo recording and run one-step undo or redo.",
00241:                         Cmd("Enable Full Undo Recording", "CE_UNDOSETTINGS ", "Enable AutoCAD full undo recording."),
00242:                         Cmd("Undo One Step", "CE_UNDO ", "Run one native AutoCAD undo step."),
00243:                         Cmd("Redo One Step", "CE_REDO ", "Run one native AutoCAD redo step.")),
00244:                     Menu(
00245:                         "CE_TOOLS_COMMAND_CATALOGUE_MENU",
00246:                         "Command\nCatalogue",
00247:                         "Search, audit and export every command declared by CE Tools.",
00248:                         Cmd("Open All Commands", "CE_COMMANDCENTER ", "Open the searchable all-command workflow centre."),
00249:                         Cmd("Command Report", "CE_COMMANDREPORT ", "Review every command, module and ribbon assignment."),
00250:                         Cmd("Command Audit", "CE_COMMANDAUDIT ", "Audit unique declarations and ribbon coverage."),
00251:                         Cmd("Export Command CSV", "CE_COMMANDEXPORT ", "Export the command catalogue to CSV."),
00252:                         Cmd("Export Searchable HTML", "CE_COMMANDHTML ", "Create a searchable offline command reference."),
00253:                         Cmd("Refresh Ribbon and Catalogue", "CE_RIBBONREFRESH ", "Rebuild the CE Tools ribbon and reload the command catalogue.")),
00254:                     Menu(
00255:                         "CE_TOOLS_SETTINGS_CENTRE_MENU",
00256:                         "Settings &\nRelease",
00257:                         "Open all configuration workflows and verify the exact installed release.",
00258:                         Cmd("Settings Centre", "CE_SETTINGS ", "Open every discipline configuration workflow in one searchable window."),
00259:                         Cmd("Settings Coverage Audit", "CE_SETTINGSAUDIT ", "Review the configuration workflows exposed by the settings centre."),
00260:                         Cmd("Installed Version", "CE_VERSION ", "Review the loaded assembly, source commit and bundle identity."),
00261:                         Cmd("Verify Installation", "CE_INSTALLVERIFY ", "Verify packaged files against the installed SHA-256 release manifest."),
```

### Lines 759-845
```csharp
00759:                         Cmd("Pump Curve Template", "CE_PUMPCURVETEMPLATE ", "Create a pump-curve input template."),
00760:                         Cmd("Project Presentation Tools", "CE_PROJECTPRESENTATIONTOOLS ", "Open project presentation workflows."),
00761:                         Cmd("Create Project Presentation", "CE_PRESENTATIONCREATE ", "Create a presentation package."),
00762:                         Cmd("Preview Project Presentation", "CE_PRESENTATIONPREVIEW ", "Preview generated presentation content."),
00763:                         Cmd("Ribbon Icon Settings", "CE_RIBBONICONS ", "Review or configure CE Tools ribbon icon mode."))));
00764:         }
00765: 
00766:         private static void AddProductionPanel(RibbonTab tab)
00767:         {
00768:             AddPanel(
00769:                 tab,
00770:                 ProductionPanelId,
00771:                 "Production",
00772:                 Row(
00773:                     Menu(
00774:                         "CE_TOOLS_CLIENT_BOOK_MENU",
00775:                         "Project Closeout\nClient Book",
00776:                         "Create linked A4/A3 client summary books at project closeout.",
00777:                         Cmd("Project Closeout - A4 and A3", "CE_PROJECTCLOSEOUT ", "Create or refresh the complete A4 and A3 client summary books."),
00778:                         Cmd("Create Client Book", "CE_CLIENTBOOK ", "Choose A4, A3 or both and create linked summary pages."),
00779:                         Cmd("Refresh Client Book", "CE_CLIENTBOOKREFRESH ", "Refresh all linked client-book pages from current project information."),
00780:                         Cmd("Client Book Information", "CE_CLIENTBOOKINFO ", "Review page links, issue stage, revision and stale generated handles."),
00781:                         Cmd("Export Client Book Index", "CE_CLIENTBOOKINDEX ", "Export the linked client-book register to Excel.")),
00782:                     Menu(
00783:                         "CE_TOOLS_PRODUCTION_MENU",
00784:                         "Summary &\nDrawing Books",
00785:                         "Generate project summary sheets and A-series client/construction drawing-book layouts.",
00786:                         Cmd("Production Tools", "CE_REPORTTOOLS ", "Open reports, summaries and drawing-book workflows."),
00787:                         Cmd("Create Project Summary Sheet", "CE_SUMMARYSHEET ", "Create a linked project metadata, discipline and production-readiness summary."),
00788:                         Cmd("Refresh Project Summary", "CE_SUMMARYREFRESH ", "Refresh the summary from current model, links and layouts."),
00789:                         Cmd("Summary Link Information", "CE_SUMMARYINFO ", "Review summary anchor and generated-object link status."),
00790:                         Cmd("Create A-Series Drawing Books", "CE_DRAWINGBOOK ", "Create or refresh A4/A3 client and A1/A0 construction layouts."),
00791:                         Cmd("Export Drawing Book Index", "CE_BOOKINDEX ", "Export the standard and existing layout register to Excel."))));
00792:         }
00793: 
00794:         private static RibbonRow Row(params RibbonItem[] items)
00795:         {
00796:             var row = new RibbonRow();
00797:             foreach (RibbonItem item in items) row.RowItems.Add(item);
00798:             return row;
00799:         }
00800: 
00801:         private static void AddPanel(
00802:             RibbonTab tab,
00803:             string panelId,
00804:             string title,
00805:             params RibbonRow[] rows)
00806:         {
00807:             var source = new RibbonPanelSource
00808:             {
00809:                 Id = panelId,
00810:                 Title = title.ToUpperInvariant()
00811:             };
00812:             foreach (RibbonRow row in rows) source.Rows.Add(row);
00813:             tab.Panels.Add(new RibbonPanel { Source = source });
00814:         }
00815: 
00816:         private static RibbonMenuButton Menu(
00817:             string id,
00818:             string text,
00819:             string toolTip,
00820:             params RibbonCommandDefinition[] commands)
00821:         {
00822:             var menu = new RibbonMenuButton
00823:             {
00824:                 Id = id,
00825:                 Text = text,
00826:                 ShowText = true,
00827:                 ShowImage = true,
00828:                 Size = RibbonItemSize.Large,
00829:                 Image = RibbonVisuals.Small(id),
00830:                 LargeImage = RibbonVisuals.Large(id),
00831:                 ToolTip = toolTip
00832:             };
00833:             int commandIndex = 0;
00834:             foreach (RibbonCommandDefinition command in commands)
00835:                 menu.Items.Add(CreateCommandButton(command, id, commandIndex++));
00836:             return menu;
00837:         }
00838: 
00839:         private static RibbonCommandDefinition Cmd(
00840:             string text,
00841:             string command,
00842:             string toolTip)
00843:         {
00844:             return new RibbonCommandDefinition(text, command, toolTip);
00845:         }
```

## PolylineDirectionCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 501-581
```csharp
00501:             }
00502:             return !string.IsNullOrWhiteSpace(sourceHandle);
00503:         }
00504: 
00505:         private static Point3d AverageArrowPoints(AutoCADSolid arrow)
00506:         {
00507:             Point3d first = arrow.GetPointAt(0);
00508:             Point3d second = arrow.GetPointAt(1);
00509:             Point3d tip = arrow.GetPointAt(2);
00510:             return new Point3d(
00511:                 (first.X + second.X + tip.X) / 3.0,
00512:                 (first.Y + second.Y + tip.Y) / 3.0,
00513:                 (first.Z + second.Z + tip.Z) / 3.0);
00514:         }
00515: 
00516:         private static void ClearArrows(Document document)
00517:         {
00518:             Editor editor = document.Editor;
00519:             var scopeOptions = new PromptKeywordOptions(
00520:                 "\nClear CE direction arrows [SelectedPolylines/All] <SelectedPolylines>: ")
00521:             {
00522:                 AllowNone = true
00523:             };
00524:             scopeOptions.Keywords.Add("SelectedPolylines");
00525:             scopeOptions.Keywords.Add("All");
00526: 
00527:             PromptResult scopeResult = editor.GetKeywords(scopeOptions);
00528:             if (scopeResult.Status == PromptStatus.Cancel)
00529:             {
00530:                 return;
00531:             }
00532: 
00533:             bool clearAll = scopeResult.Status == PromptStatus.OK &&
00534:                             string.Equals(
00535:                                 scopeResult.StringResult,
00536:                                 "All",
00537:                                 StringComparison.OrdinalIgnoreCase);
00538: 
00539:             var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
00540:             if (!clearAll)
00541:             {
00542:                 PromptSelectionResult selection = GetPolylineSelection(
00543:                     editor,
00544:                     "\nSelect polylines whose CE direction arrows must be removed: ");
00545:                 if (selection.Status != PromptStatus.OK ||
00546:                     selection.Value == null ||
00547:                     selection.Value.Count == 0)
00548:                 {
00549:                     return;
00550:                 }
00551: 
00552:                 using (Transaction readTransaction =
00553:                     document.Database.TransactionManager.StartTransaction())
00554:                 {
00555:                     foreach (ObjectId objectId in selection.Value.GetObjectIds())
00556:                     {
00557:                         Curve curve = readTransaction.GetObject(
00558:                             objectId,
00559:                             OpenMode.ForRead,
00560:                             false) as Curve;
00561:                         if (IsSupportedPolyline(curve))
00562:                         {
00563:                             handles.Add(curve.Handle.ToString());
00564:                         }
00565:                     }
00566:                 }
00567: 
00568:                 if (handles.Count == 0)
00569:                 {
00570:                     editor.WriteMessage(
00571:                         "\nCE_PLDIRCLEAR: no supported polylines were selected.");
00572:                     return;
00573:                 }
00574:             }
00575: 
00576:             if (!Confirm(
00577:                     editor,
00578:                     clearAll
00579:                         ? "Remove every CE polyline direction arrow in the current space"
00580:                         : "Remove CE direction arrows linked to the selected polylines"))
00581:             {
```

### Lines 785-819
```csharp
00785:             if (table.Has(RegAppName))
00786:             {
00787:                 return;
00788:             }
00789: 
00790:             table.UpgradeOpen();
00791:             var record = new RegAppTableRecord { Name = RegAppName };
00792:             table.Add(record);
00793:             transaction.AddNewlyCreatedDBObject(record, true);
00794:         }
00795: 
00796:         private static double GetDefaultArrowSize(Database database)
00797:         {
00798:             return 1.0;
00799:         }
00800: 
00801:         private static bool Confirm(Editor editor, string message)
00802:         {
00803:             var options = new PromptKeywordOptions(
00804:                 "\n" + message + "? [Yes/No] <No>: ")
00805:             {
00806:                 AllowNone = true
00807:             };
00808:             options.Keywords.Add("Yes");
00809:             options.Keywords.Add("No");
00810: 
00811:             PromptResult result = editor.GetKeywords(options);
00812:             return result.Status == PromptStatus.OK &&
00813:                    string.Equals(
00814:                        result.StringResult,
00815:                        "Yes",
00816:                        StringComparison.OrdinalIgnoreCase);
00817:         }
00818:     }
00819: }
```

## ProductionCommentCommands.cs
Hits: `CE_DRAWINGBOOK`, `CE_BOOKINDEX`, `CE_CLIENTBOOK`, `CE_PROJECTCLOSEOUT`

### Lines 62-162
```csharp
00062:                     new ProductionChoice("Profile popup report", "CE_PROFILEREPORT2 "),
00063:                     new ProductionChoice("Surface popup report", "CE_SURFACEREPORT2 "),
00064:                     new ProductionChoice("Refresh all linked outputs before reporting", "CE_REFRESHALL ")
00065:                 });
00066:         }
00067: 
00068:         [CommandMethod("CE_TOOLS", "CE_PRODUCTIONCENTER", CommandFlags.Modal | CommandFlags.Redraw)]
00069:         public void ProductionCentre()
00070:         {
00071:             RunChoiceWindow(
00072:                 "CE Tools - Plan Production and Project Books",
00073:                 "Create or refresh client/construction books, summaries, registers and PDFs from one production window.",
00074:                 new List<ProductionChoice>
00075:                 {
00076:                     new ProductionChoice("Refresh all dynamic model data first", "CE_REFRESHALL "),
00077:                     new ProductionChoice("Create or refresh project summary sheet", "CE_SUMMARYSHEET "),
00078:                     new ProductionChoice("Refresh existing project summary", "CE_SUMMARYREFRESH "),
00079:                     new ProductionChoice("Review project summary links", "CE_SUMMARYINFO "),
00080:                     new ProductionChoice("Project closeout - create A4 and A3 client books", "CE_PROJECTCLOSEOUT "),
00081:                     new ProductionChoice("Create A4, A3 or both client summary books", "CE_CLIENTBOOK "),
00082:                     new ProductionChoice("Refresh all linked client-book pages", "CE_CLIENTBOOKREFRESH "),
00083:                     new ProductionChoice("Review client-book link and revision information", "CE_CLIENTBOOKINFO "),
00084:                     new ProductionChoice("Export client-book register to Excel", "CE_CLIENTBOOKINDEX "),
00085:                     new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),
00086:                     new ProductionChoice("Create A4/A3 client and A1/A0 construction layouts", "CE_DRAWINGBOOK "),
00087:                     new ProductionChoice("Export drawing-book layout register to Excel", "CE_BOOKINDEX "),
00088:                     new ProductionChoice("Open AutoCAD Publish for batch PDF output", "CE_BATCHPUBLISH "),
00089:                     new ProductionChoice("Show where CE books and exports are stored", "CE_OUTPUTLOCATION ")
00090:                 });
00091:         }
00092: 
00093:         [CommandMethod("CE_TOOLS", "CE_PRINTCENTER", CommandFlags.Modal | CommandFlags.Redraw)]
00094:         public void PrintCentre()
00095:         {
00096:             RunChoiceWindow(
00097:                 "CE Tools - Print and Publish Centre",
00098:                 "Prepare linked books first, then use AutoCAD's native plot or publish workflows for PDF or hard-copy output.",
00099:                 new List<ProductionChoice>
00100:                 {
00101:                     new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),
00102:                     new ProductionChoice("Create/refresh A-series drawing-book layouts", "CE_DRAWINGBOOK "),
00103:                     new ProductionChoice("Create/refresh A4/A3 client books", "CE_CLIENTBOOK "),
00104:                     new ProductionChoice("Refresh client-book pages", "CE_CLIENTBOOKREFRESH "),
00105:                     new ProductionChoice("Open AutoCAD Publish for batch PDF", "CE_BATCHPUBLISH "),
00106:                     new ProductionChoice("Open AutoCAD Plot for current sheet", "_.PLOT "),
00107:                     new ProductionChoice("Export drawing-book index", "CE_BOOKINDEX "),
00108:                     new ProductionChoice("Export client-book index", "CE_CLIENTBOOKINDEX "),
00109:                     new ProductionChoice("Show output locations", "CE_OUTPUTLOCATION ")
00110:                 });
00111:         }
00112: 
00113:         [CommandMethod("CE_TOOLS", "CE_BATCHPUBLISH", CommandFlags.Modal | CommandFlags.Redraw)]
00114:         public void BatchPublish()
00115:         {
00116:             Document document = ActiveDocument();
00117:             if (document == null) return;
00118:             document.Editor.WriteMessage("\nCE_BATCHPUBLISH is opening AutoCAD Publish. Select the generated A1/A0 construction layouts or A4/A3 client-book layouts and choose a PDF publish setup.");
00119:             document.SendStringToExecute("_.PUBLISH ", true, false, true);
00120:         }
00121: 
00122:         [CommandMethod("CE_TOOLS", "CE_OUTPUTLOCATION", CommandFlags.Modal | CommandFlags.Redraw)]
00123:         public void OutputLocation()
00124:         {
00125:             Document document = ActiveDocument();
00126:             if (document == null) return;
00127:             string drawingPath = document.Database.Filename;
00128:             string folder = string.IsNullOrWhiteSpace(drawingPath) ? "<Drawing has not been saved>" : Path.GetDirectoryName(drawingPath);
00129:             int layouts = CountLayouts(document.Database);
00130:             var rows = new List<IList<string>>
00131:             {
00132:                 new List<string> { "Current DWG", string.IsNullOrWhiteSpace(drawingPath) ? "<Unsaved drawing>" : drawingPath },
00133:                 new List<string> { "Drawing folder", string.IsNullOrWhiteSpace(folder) ? "<Unavailable>" : folder },
00134:                 new List<string> { "A-series drawing books", "Stored as layouts inside the current DWG until plotted or published" },
00135:                 new List<string> { "A4/A3 client books", "Stored as linked layouts/pages inside the current DWG" },
00136:                 new List<string> { "Current layout count", layouts.ToString(System.Globalization.CultureInfo.InvariantCulture) },
00137:                 new List<string> { "BOQ and report Excel files", "Saved to the location selected in the export dialog" },
00138:                 new List<string> { "Published PDFs", "Saved to the path selected in AutoCAD Publish/Plot" },
00139:                 new List<string> { "Recommended project output folder", string.IsNullOrWhiteSpace(folder) ? "Save the DWG first" : Path.Combine(folder, "CE Tools Outputs") }
00140:             };
00141:             GridReportPresenter.ShowReportAndOfferTable(
00142:                 document,
00143:                 "CE Tools - Output Locations",
00144:                 "CE drawing and client books are linked DWG layouts. Excel and PDF paths are selected when exporting or publishing.",
00145:                 new List<string> { "Output", "Location / Behaviour" },
00146:                 rows,
00147:                 "CE TOOLS OUTPUT LOCATIONS");
00148:         }
00149: 
00150:         private static int CountLayouts(Database database)
00151:         {
00152:             int count = 0;
00153:             using (Transaction transaction = database.TransactionManager.StartTransaction())
00154:             {
00155:                 DBDictionary layouts = transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead, false) as DBDictionary;
00156:                 if (layouts != null)
00157:                 {
00158:                     foreach (DBDictionaryEntry entry in layouts)
00159:                     {
00160:                         Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
00161:                         if (layout != null && !layout.ModelType) count++;
00162:                     }
```

## ProductionDrawingRegisterCommands.cs
Hits: `DrawingRegister`, `GetString(`

### Lines 1-531
```csharp
00001: using System;
00002: using System.Collections.Generic;
00003: using System.Collections.ObjectModel;
00004: using System.Globalization;
00005: using System.IO;
00006: using System.Linq;
00007: using System.Reflection;
00008: using System.Text;
00009: using System.Windows;
00010: using System.Windows.Controls;
00011: using System.Windows.Data;
00012: using Autodesk.AutoCAD.ApplicationServices;
00013: using Autodesk.AutoCAD.DatabaseServices;
00014: using Autodesk.AutoCAD.Geometry;
00015: using Autodesk.AutoCAD.Runtime;
00016: using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
00017: 
00018: [assembly: CommandClass(typeof(CETools.Civil3D.ProductionDrawingRegisterCommands))]
00019: 
00020: namespace CETools.Civil3D
00021: {
00022:     public sealed class ProductionDrawingRegisterCommands
00023:     {
00024:         [CommandMethod("CE_TOOLS", "CE_DRAWINGREGISTEREDIT", CommandFlags.Modal | CommandFlags.Redraw)]
00025:         public void EditDrawingRegister()
00026:         {
00027:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00028:             if (document == null) return;
00029:             ProductionDrawingRegisterData result;
00030:             EditForProduction(
00031:                 document,
00032:                 ReadLayoutSeeds(document.Database),
00033:                 "Save Register",
00034:                 out result);
00035:         }
00036: 
00037:         internal static bool EditForProduction(
00038:             Document document,
00039:             IEnumerable<ProductionDrawingSeed> seeds,
00040:             string actionText,
00041:             out ProductionDrawingRegisterData result)
00042:         {
00043:             result = null;
00044:             if (document == null) return false;
00045:             ProductionDrawingRegisterData data = ProductionDrawingRegisterStore.Read(
00046:                 document.Database);
00047:             IDictionary<string, string> project =
00048:                 ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
00049:             data.ApplyProjectDefaults(project);
00050:             data.MergeSeeds(seeds ?? Enumerable.Empty<ProductionDrawingSeed>());
00051:             data.ApplyRowDefaults();
00052: 
00053:             var window = new ProductionDrawingRegisterWindow(
00054:                 data,
00055:                 string.IsNullOrWhiteSpace(actionText)
00056:                     ? "Save"
00057:                     : actionText);
00058:             AcApplication.ShowModalWindow(window);
00059:             if (!window.Accepted) return false;
00060: 
00061:             result = window.BuildResult();
00062:             result.ApplyRowDefaults();
00063:             ProductionDrawingRegisterStore.Write(document.Database, result);
00064:             ProjectSetupCommands.MergeSharedProjectMetadata(
00065:                 document.Database,
00066:                 result.Headers);
00067:             ProjectSetupCommands.RefreshInformationTables(document);
00068:             document.Editor.WriteMessage(
00069:                 "\nCE drawing register saved. Rows={0}; title metadata is linked to production layouts and exports.",
00070:                 result.Rows.Count);
00071:             return true;
00072:         }
00073: 
00074:         internal static List<ProductionDrawingSeed> ReadLayoutSeeds(Database database)
00075:         {
00076:             var result = new List<ProductionDrawingSeed>();
00077:             using (Transaction transaction =
00078:                 database.TransactionManager.StartTransaction())
00079:             {
00080:                 DBDictionary layouts = transaction.GetObject(
00081:                     database.LayoutDictionaryId,
00082:                     OpenMode.ForRead,
00083:                     false) as DBDictionary;
00084:                 if (layouts == null) return result;
00085:                 foreach (DBDictionaryEntry entry in layouts)
00086:                 {
00087:                     Layout layout = transaction.GetObject(
00088:                         entry.Value,
00089:                         OpenMode.ForRead,
00090:                         false) as Layout;
00091:                     if (layout == null || layout.ModelType) continue;
00092:                     result.Add(new ProductionDrawingSeed(
00093:                         layout.LayoutName,
00094:                         layout.LayoutName,
00095:                         "Project drawing",
00096:                         "Existing",
00097:                         "As shown"));
00098:                 }
00099:             }
00100:             return result;
00101:         }
00102:     }
00103: 
00104:     internal sealed class ProductionDrawingSeed
00105:     {
00106:         internal ProductionDrawingSeed(
00107:             string layout,
00108:             string title,
00109:             string purpose,
00110:             string paper,
00111:             string scale)
00112:         {
00113:             Layout = layout ?? string.Empty;
00114:             Title = title ?? string.Empty;
00115:             Purpose = purpose ?? string.Empty;
00116:             Paper = paper ?? string.Empty;
00117:             Scale = scale ?? string.Empty;
00118:         }
00119:         internal string Layout { get; private set; }
00120:         internal string Title { get; private set; }
00121:         internal string Purpose { get; private set; }
00122:         internal string Paper { get; private set; }
00123:         internal string Scale { get; private set; }
00124:     }
00125: 
00126:     internal sealed class ProductionDrawingRegisterRow
00127:     {
00128:         public string DrawingNumber { get; set; }
00129:         public string Layout { get; set; }
00130:         public string Title { get; set; }
00131:         public string Purpose { get; set; }
00132:         public string Paper { get; set; }
00133:         public string Scale { get; set; }
00134:         public string Stage { get; set; }
00135:         public string Revision { get; set; }
00136:         public string IssueDate { get; set; }
00137: 
00138:         internal ProductionDrawingRegisterRow Clone()
00139:         {
00140:             return new ProductionDrawingRegisterRow
00141:             {
00142:                 DrawingNumber = DrawingNumber ?? string.Empty,
00143:                 Layout = Layout ?? string.Empty,
00144:                 Title = Title ?? string.Empty,
00145:                 Purpose = Purpose ?? string.Empty,
00146:                 Paper = Paper ?? string.Empty,
00147:                 Scale = Scale ?? string.Empty,
00148:                 Stage = Stage ?? string.Empty,
00149:                 Revision = Revision ?? string.Empty,
00150:                 IssueDate = IssueDate ?? string.Empty
00151:             };
00152:         }
00153:     }
00154: 
00155:     internal sealed class ProductionDrawingRegisterData
00156:     {
00157:         internal static readonly string[] HeaderFields =
00158:         {
00159:             "Project Name",
00160:             "Project Number",
00161:             "Client",
00162:             "Company",
00163:             "Project Stage",
00164:             "Revision",
00165:             "Issue Date",
00166:             "Drawing Number Prefix",
00167:             "Designed By",
00168:             "Drawn By",
00169:             "Checked By",
00170:             "Approved By",
00171:             "Title Block Source"
00172:         };
00173: 
00174:         internal ProductionDrawingRegisterData()
00175:         {
00176:             Headers = new Dictionary<string, string>(
00177:                 StringComparer.OrdinalIgnoreCase);
00178:             foreach (string field in HeaderFields) Headers[field] = string.Empty;
00179:             Rows = new List<ProductionDrawingRegisterRow>();
00180:         }
00181: 
00182:         internal IDictionary<string, string> Headers { get; private set; }
00183:         internal List<ProductionDrawingRegisterRow> Rows { get; private set; }
00184: 
00185:         internal string Header(string name)
00186:         {
00187:             string value;
00188:             return Headers.TryGetValue(name, out value)
00189:                 ? value ?? string.Empty
00190:                 : string.Empty;
00191:         }
00192: 
00193:         internal void ApplyProjectDefaults(IDictionary<string, string> project)
00194:         {
00195:             foreach (string field in HeaderFields)
00196:             {
00197:                 if (string.Equals(field, "Title Block Source", StringComparison.OrdinalIgnoreCase))
00198:                     continue;
00199:                 string existing = Header(field);
00200:                 string value;
00201:                 if (string.IsNullOrWhiteSpace(existing) &&
00202:                     project != null && project.TryGetValue(field, out value))
00203:                     Headers[field] = value ?? string.Empty;
00204:             }
00205:             if (string.IsNullOrWhiteSpace(Header("Issue Date")))
00206:                 Headers["Issue Date"] = DateTime.Today.ToString(
00207:                     "yyyy-MM-dd",
00208:                     CultureInfo.InvariantCulture);
00209:             if (string.IsNullOrWhiteSpace(Header("Drawing Number Prefix")))
00210:                 Headers["Drawing Number Prefix"] = "CE";
00211:             if (string.IsNullOrWhiteSpace(Header("Title Block Source")))
00212:             {
00213:                 string bundled = ProductionTitleBlockManager.FindBundledSource();
00214:                 if (!string.IsNullOrWhiteSpace(bundled))
00215:                     Headers["Title Block Source"] = bundled;
00216:             }
00217:         }
00218: 
00219:         internal void MergeSeeds(IEnumerable<ProductionDrawingSeed> seeds)
00220:         {
00221:             foreach (ProductionDrawingSeed seed in seeds)
00222:             {
00223:                 if (seed == null || string.IsNullOrWhiteSpace(seed.Layout)) continue;
00224:                 ProductionDrawingRegisterRow row = Find(seed.Layout);
00225:                 if (row == null)
00226:                 {
00227:                     row = new ProductionDrawingRegisterRow
00228:                     {
00229:                         Layout = seed.Layout,
00230:                         Title = seed.Title,
00231:                         Purpose = seed.Purpose,
00232:                         Paper = seed.Paper,
00233:                         Scale = seed.Scale
00234:                     };
00235:                     Rows.Add(row);
00236:                 }
00237:                 else
00238:                 {
00239:                     if (string.IsNullOrWhiteSpace(row.Title)) row.Title = seed.Title;
00240:                     if (string.IsNullOrWhiteSpace(row.Purpose)) row.Purpose = seed.Purpose;
00241:                     if (string.IsNullOrWhiteSpace(row.Paper)) row.Paper = seed.Paper;
00242:                     if (string.IsNullOrWhiteSpace(row.Scale)) row.Scale = seed.Scale;
00243:                 }
00244:             }
00245:         }
00246: 
00247:         internal void ApplyRowDefaults()
00248:         {
00249:             string prefix = Header("Drawing Number Prefix");
00250:             string stage = Header("Project Stage");
00251:             string revision = Header("Revision");
00252:             string issueDate = Header("Issue Date");
00253:             int next = 1;
00254:             foreach (ProductionDrawingRegisterRow row in Rows)
00255:             {
00256:                 if (string.IsNullOrWhiteSpace(row.DrawingNumber))
00257:                     row.DrawingNumber = (string.IsNullOrWhiteSpace(prefix) ? "CE" : prefix) +
00258:                         "-" + next.ToString("000", CultureInfo.InvariantCulture);
00259:                 if (string.IsNullOrWhiteSpace(row.Title)) row.Title = row.Layout;
00260:                 if (string.IsNullOrWhiteSpace(row.Purpose)) row.Purpose = "Project drawing";
00261:                 if (string.IsNullOrWhiteSpace(row.Scale)) row.Scale = "As shown";
00262:                 if (string.IsNullOrWhiteSpace(row.Stage)) row.Stage = stage;
00263:                 if (string.IsNullOrWhiteSpace(row.Revision)) row.Revision = revision;
00264:                 if (string.IsNullOrWhiteSpace(row.IssueDate)) row.IssueDate = issueDate;
00265:                 next++;
00266:             }
00267:         }
00268: 
00269:         internal ProductionDrawingRegisterRow Find(string layout)
00270:         {
00271:             return Rows.FirstOrDefault(row => string.Equals(
00272:                 row.Layout,
00273:                 layout,
00274:                 StringComparison.OrdinalIgnoreCase));
00275:         }
00276: 
00277:         internal ProductionDrawingRegisterData Clone()
00278:         {
00279:             var result = new ProductionDrawingRegisterData();
00280:             foreach (KeyValuePair<string, string> pair in Headers)
00281:                 result.Headers[pair.Key] = pair.Value ?? string.Empty;
00282:             result.Rows.Clear();
00283:             result.Rows.AddRange(Rows.Select(row => row.Clone()));
00284:             return result;
00285:         }
00286:     }
00287: 
00288:     internal static class ProductionDrawingRegisterStore
00289:     {
00290:         private const string RootName = "CE_TOOLS";
00291:         private const string RecordName = "DRAWING_REGISTER_METADATA";
00292: 
00293:         internal static ProductionDrawingRegisterData Read(Database database)
00294:         {
00295:             var result = new ProductionDrawingRegisterData();
00296:             if (database == null) return result;
00297:             using (Transaction transaction =
00298:                 database.TransactionManager.StartTransaction())
00299:             {
00300:                 DBDictionary named = transaction.GetObject(
00301:                     database.NamedObjectsDictionaryId,
00302:                     OpenMode.ForRead,
00303:                     false) as DBDictionary;
00304:                 if (named == null || !named.Contains(RootName)) return result;
00305:                 DBDictionary root = transaction.GetObject(
00306:                     named.GetAt(RootName),
00307:                     OpenMode.ForRead,
00308:                     false) as DBDictionary;
00309:                 if (root == null || !root.Contains(RecordName)) return result;
00310:                 Xrecord record = transaction.GetObject(
00311:                     root.GetAt(RecordName),
00312:                     OpenMode.ForRead,
00313:                     false) as Xrecord;
00314:                 if (record == null || record.Data == null) return result;
00315:                 foreach (TypedValue value in record.Data)
00316:                 {
00317:                     string text = value.Value as string;
00318:                     if (string.IsNullOrWhiteSpace(text)) continue;
00319:                     string[] parts = text.Split('|');
00320:                     if (parts.Length == 3 && parts[0] == "H")
00321:                         result.Headers[Decode(parts[1])] = Decode(parts[2]);
00322:                     else if (parts.Length == 10 && parts[0] == "R")
00323:                     {
00324:                         result.Rows.Add(new ProductionDrawingRegisterRow
00325:                         {
00326:                             DrawingNumber = Decode(parts[1]),
00327:                             Layout = Decode(parts[2]),
00328:                             Title = Decode(parts[3]),
00329:                             Purpose = Decode(parts[4]),
00330:                             Paper = Decode(parts[5]),
00331:                             Scale = Decode(parts[6]),
00332:                             Stage = Decode(parts[7]),
00333:                             Revision = Decode(parts[8]),
00334:                             IssueDate = Decode(parts[9])
00335:                         });
00336:                     }
00337:                 }
00338:             }
00339:             return result;
00340:         }
00341: 
00342:         internal static void Write(
00343:             Database database,
00344:             ProductionDrawingRegisterData data)
00345:         {
00346:             using (Transaction transaction =
00347:                 database.TransactionManager.StartTransaction())
00348:             {
00349:                 DBDictionary named = transaction.GetObject(
00350:                     database.NamedObjectsDictionaryId,
00351:                     OpenMode.ForWrite,
00352:                     false) as DBDictionary;
00353:                 DBDictionary root;
00354:                 if (named.Contains(RootName))
00355:                     root = transaction.GetObject(
00356:                         named.GetAt(RootName),
00357:                         OpenMode.ForWrite,
00358:                         false) as DBDictionary;
00359:                 else
00360:                 {
00361:                     root = new DBDictionary();
00362:                     named.SetAt(RootName, root);
00363:                     transaction.AddNewlyCreatedDBObject(root, true);
00364:                 }
00365:                 Xrecord record;
00366:                 if (root.Contains(RecordName))
00367:                     record = transaction.GetObject(
00368:                         root.GetAt(RecordName),
00369:                         OpenMode.ForWrite,
00370:                         false) as Xrecord;
00371:                 else
00372:                 {
00373:                     record = new Xrecord();
00374:                     root.SetAt(RecordName, record);
00375:                     transaction.AddNewlyCreatedDBObject(record, true);
00376:                 }
00377:                 var values = new List<TypedValue>
00378:                 {
00379:                     new TypedValue((int)DxfCode.Text, "SCHEMA|1")
00380:                 };
00381:                 foreach (string field in ProductionDrawingRegisterData.HeaderFields)
00382:                     values.Add(new TypedValue(
00383:                         (int)DxfCode.Text,
00384:                         "H|" + Encode(field) + "|" + Encode(data.Header(field))));
00385:                 foreach (ProductionDrawingRegisterRow row in data.Rows)
00386:                 {
00387:                     values.Add(new TypedValue(
00388:                         (int)DxfCode.Text,
00389:                         string.Join("|", new[]
00390:                         {
00391:                             "R",
00392:                             Encode(row.DrawingNumber),
00393:                             Encode(row.Layout),
00394:                             Encode(row.Title),
00395:                             Encode(row.Purpose),
00396:                             Encode(row.Paper),
00397:                             Encode(row.Scale),
00398:                             Encode(row.Stage),
00399:                             Encode(row.Revision),
00400:                             Encode(row.IssueDate)
00401:                         })));
00402:                 }
00403:                 record.Data = new ResultBuffer(values.ToArray());
00404:                 transaction.Commit();
00405:             }
00406:         }
00407: 
00408:         private static string Encode(string value)
00409:         {
00410:             return Convert.ToBase64String(
00411:                 Encoding.UTF8.GetBytes(value ?? string.Empty));
00412:         }
00413: 
00414:         private static string Decode(string value)
00415:         {
00416:             try
00417:             {
00418:                 return Encoding.UTF8.GetString(
00419:                     Convert.FromBase64String(value ?? string.Empty));
00420:             }
00421:             catch
00422:             {
00423:                 return string.Empty;
00424:             }
00425:         }
00426:     }
00427: 
00428:     internal sealed class ProductionDrawingRegisterWindow : Window
00429:     {
00430:         private readonly IDictionary<string, TextBox> _headers =
00431:             new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
00432:         private readonly ObservableCollection<ProductionDrawingRegisterRow> _rows;
00433:         private readonly DataGrid _grid;
00434: 
00435:         internal ProductionDrawingRegisterWindow(
00436:             ProductionDrawingRegisterData source,
00437:             string actionText)
00438:         {
00439:             Title = "CE Tools - Drawing Titles and Register";
00440:             Width = 1180;
00441:             Height = 760;
00442:             MinWidth = 860;
00443:             MinHeight = 560;
00444:             WindowStartupLocation = WindowStartupLocation.CenterOwner;
00445:             ResizeMode = ResizeMode.CanResizeWithGrip;
00446: 
00447:             _rows = new ObservableCollection<ProductionDrawingRegisterRow>(
00448:                 source.Rows.Select(row => row.Clone()));
00449:             var root = new DockPanel { Margin = new Thickness(14) };
00450:             Content = root;
00451: 
00452:             var buttons = new StackPanel
00453:             {
00454:                 Orientation = Orientation.Horizontal,
00455:                 HorizontalAlignment = HorizontalAlignment.Right,
00456:                 Margin = new Thickness(0, 10, 0, 0)
00457:             };
00458:             DockPanel.SetDock(buttons, Dock.Bottom);
00459:             root.Children.Add(buttons);
00460:             var add = Button("Add Drawing", 105);
00461:             add.Click += delegate
00462:             {
00463:                 _rows.Add(new ProductionDrawingRegisterRow
00464:                 {
00465:                     Stage = Value("Project Stage"),
00466:                     Revision = Value("Revision"),
00467:                     IssueDate = Value("Issue Date"),
00468:                     Scale = "As shown"
00469:                 });
00470:             };
00471:             buttons.Children.Add(add);
00472:             var remove = Button("Remove Selected", 125);
00473:             remove.Margin = new Thickness(6, 0, 0, 0);
00474:             remove.Click += delegate
00475:             {
00476:                 ProductionDrawingRegisterRow row =
00477:                     _grid.SelectedItem as ProductionDrawingRegisterRow;
00478:                 if (row != null) _rows.Remove(row);
00479:             };
00480:             buttons.Children.Add(remove);
00481:             var cancel = Button("Cancel", 90);
00482:             cancel.IsCancel = true;
00483:             cancel.Margin = new Thickness(18, 0, 0, 0);
00484:             cancel.Click += delegate { DialogResult = false; };
00485:             buttons.Children.Add(cancel);
00486:             var save = Button(actionText, 145);
00487:             save.IsDefault = true;
00488:             save.Margin = new Thickness(6, 0, 0, 0);
00489:             save.Click += delegate
00490:             {
00491:                 _grid.CommitEdit(DataGridEditingUnit.Cell, true);
00492:                 _grid.CommitEdit(DataGridEditingUnit.Row, true);
00493:                 if (_rows.Any(row => string.IsNullOrWhiteSpace(row.Layout)))
00494:                 {
00495:                     MessageBox.Show(
00496:                         "Every drawing-register row must have a layout name.",
00497:                         "CE Tools",
00498:                         MessageBoxButton.OK,
00499:                         MessageBoxImage.Warning);
00500:                     return;
00501:                 }
00502:                 Accepted = true;
00503:                 DialogResult = true;
00504:             };
00505:             buttons.Children.Add(save);
00506: 
00507:             var heading = new TextBlock
00508:             {
00509:                 Text = "Drawing titles, title block information and drawing register",
00510:                 FontSize = 20,
00511:                 FontWeight = FontWeights.SemiBold,
00512:                 Margin = new Thickness(0, 0, 0, 4)
00513:             };
00514:             DockPanel.SetDock(heading, Dock.Top);
00515:             root.Children.Add(heading);
00516:             var note = new TextBlock
00517:             {
00518:                 Text = "Edit project issue data and every sheet in one popup. The saved values drive drawing titles, title-block attributes, on-sheet registers and Excel indexes.",
00519:                 TextWrapping = TextWrapping.Wrap,
00520:                 Margin = new Thickness(0, 0, 0, 10)
00521:             };
00522:             DockPanel.SetDock(note, Dock.Top);
00523:             root.Children.Add(note);
00524: 
00525:             var headerGrid = BuildHeaderGrid(source);
00526:             var headerScroll = new ScrollViewer
00527:             {
00528:                 Content = headerGrid,
00529:                 Height = 215,
00530:                 VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
00531:                 Margin = new Thickness(0, 0, 0, 10)
```

### Lines 542-636
```csharp
00542:                 SelectionMode = DataGridSelectionMode.Single,
00543:                 HeadersVisibility = DataGridHeadersVisibility.Column,
00544:                 GridLinesVisibility = DataGridGridLinesVisibility.All
00545:             };
00546:             AddColumn("Drawing No.", "DrawingNumber", 110);
00547:             AddColumn("Layout", "Layout", 145);
00548:             AddColumn("Title", "Title", 220);
00549:             AddColumn("Purpose / Discipline", "Purpose", 155);
00550:             AddColumn("Paper", "Paper", 75);
00551:             AddColumn("Scale", "Scale", 85);
00552:             AddColumn("Stage", "Stage", 105);
00553:             AddColumn("Revision", "Revision", 75);
00554:             AddColumn("Issue Date", "IssueDate", 100);
00555:             root.Children.Add(_grid);
00556:         }
00557: 
00558:         internal bool Accepted { get; private set; }
00559: 
00560:         internal ProductionDrawingRegisterData BuildResult()
00561:         {
00562:             var result = new ProductionDrawingRegisterData();
00563:             foreach (string field in ProductionDrawingRegisterData.HeaderFields)
00564:                 result.Headers[field] = Value(field);
00565:             result.Rows.Clear();
00566:             result.Rows.AddRange(_rows.Select(row => row.Clone()));
00567:             return result;
00568:         }
00569: 
00570:         private Grid BuildHeaderGrid(ProductionDrawingRegisterData source)
00571:         {
00572:             var grid = new Grid();
00573:             grid.ColumnDefinitions.Add(new ColumnDefinition
00574:             {
00575:                 Width = new GridLength(175)
00576:             });
00577:             grid.ColumnDefinitions.Add(new ColumnDefinition
00578:             {
00579:                 Width = new GridLength(1, GridUnitType.Star)
00580:             });
00581:             int row = 0;
00582:             foreach (string field in ProductionDrawingRegisterData.HeaderFields)
00583:             {
00584:                 grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
00585:                 var label = new TextBlock
00586:                 {
00587:                     Text = field,
00588:                     VerticalAlignment = VerticalAlignment.Center,
00589:                     Margin = new Thickness(0, 3, 10, 3)
00590:                 };
00591:                 Grid.SetRow(label, row);
00592:                 grid.Children.Add(label);
00593:                 var editor = new TextBox
00594:                 {
00595:                     Text = source.Header(field),
00596:                     Margin = new Thickness(0, 2, 0, 2),
00597:                     Padding = new Thickness(4, 2, 4, 2)
00598:                 };
00599:                 _headers[field] = editor;
00600:                 if (string.Equals(field, "Title Block Source", StringComparison.OrdinalIgnoreCase))
00601:                 {
00602:                     var panel = new DockPanel();
00603:                     var browse = Button("Browse...", 85);
00604:                     DockPanel.SetDock(browse, Dock.Right);
00605:                     browse.Margin = new Thickness(6, 2, 0, 2);
00606:                     browse.Click += delegate
00607:                     {
00608:                         var dialog = new Microsoft.Win32.OpenFileDialog
00609:                         {
00610:                             Title = "Select CE Tools title-block source DWG",
00611:                             Filter = "AutoCAD drawing (*.dwg)|*.dwg|All files (*.*)|*.*",
00612:                             CheckFileExists = true,
00613:                             Multiselect = false
00614:                         };
00615:                         if (dialog.ShowDialog() == true)
00616:                             editor.Text = dialog.FileName;
00617:                     };
00618:                     panel.Children.Add(browse);
00619:                     panel.Children.Add(editor);
00620:                     Grid.SetRow(panel, row);
00621:                     Grid.SetColumn(panel, 1);
00622:                     grid.Children.Add(panel);
00623:                 }
00624:                 else
00625:                 {
00626:                     Grid.SetRow(editor, row);
00627:                     Grid.SetColumn(editor, 1);
00628:                     grid.Children.Add(editor);
00629:                 }
00630:                 row++;
00631:             }
00632:             return grid;
00633:         }
00634: 
00635:         private void AddColumn(string header, string path, double width)
00636:         {
```

### Lines 680-753
```csharp
00680:                     "Resources",
00681:                     "TitleBlocks",
00682:                     "CE TOOLS - TITLE BLOCKS.dwg"));
00683:                 return File.Exists(path) ? path : string.Empty;
00684:             }
00685:             catch
00686:             {
00687:                 return string.Empty;
00688:             }
00689:         }
00690: 
00691:         internal static ObjectId TryInsert(
00692:             Database destination,
00693:             Transaction transaction,
00694:             BlockTableRecord paperSpace,
00695:             string sourcePath,
00696:             string paperName,
00697:             Point3d insertion,
00698:             ProductionDrawingRegisterData register,
00699:             ProductionDrawingRegisterRow row,
00700:             out string diagnostic)
00701:         {
00702:             diagnostic = string.Empty;
00703:             if (destination == null || transaction == null || paperSpace == null ||
00704:                 string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
00705:             {
00706:                 diagnostic = "No readable title-block source DWG was selected.";
00707:                 return ObjectId.Null;
00708:             }
00709: 
00710:             try
00711:             {
00712:                 string blockName;
00713:                 using (var source = new Database(false, true))
00714:                 {
00715:                     source.ReadDwgFile(sourcePath, FileShare.Read, true, string.Empty);
00716:                     source.CloseInput(true);
00717:                     ObjectId sourceBlockId = FindBestBlock(
00718:                         source,
00719:                         paperName,
00720:                         out blockName);
00721:                     if (sourceBlockId.IsNull)
00722:                     {
00723:                         diagnostic = "No compatible " + paperName +
00724:                             " attributed block definition was found in the selected DWG.";
00725:                         return ObjectId.Null;
00726:                     }
00727:                     var ids = new ObjectIdCollection();
00728:                     ids.Add(sourceBlockId);
00729:                     var mapping = new IdMapping();
00730:                     source.WblockCloneObjects(
00731:                         ids,
00732:                         destination.BlockTableId,
00733:                         mapping,
00734:                         DuplicateRecordCloning.Replace,
00735:                         false);
00736:                 }
00737: 
00738:                 BlockTable blockTable = transaction.GetObject(
00739:                     destination.BlockTableId,
00740:                     OpenMode.ForRead,
00741:                     false) as BlockTable;
00742:                 if (blockTable == null || !blockTable.Has(blockName))
00743:                 {
00744:                     diagnostic = "The title-block definition could not be cloned into the active drawing.";
00745:                     return ObjectId.Null;
00746:                 }
00747: 
00748:                 ObjectId definitionId = blockTable[blockName];
00749:                 var reference = new BlockReference(insertion, definitionId);
00750:                 reference.SetDatabaseDefaults(destination);
00751:                 paperSpace.AppendEntity(reference);
00752:                 transaction.AddNewlyCreatedDBObject(reference, true);
00753: 
```

### Lines 818-891
```csharp
00818:                     string name = block.Name ?? string.Empty;
00819:                     if (name.IndexOf(paperName ?? string.Empty,
00820:                             StringComparison.OrdinalIgnoreCase) >= 0)
00821:                         score += 100;
00822:                     if (name.IndexOf("TITLE", StringComparison.OrdinalIgnoreCase) >= 0)
00823:                         score += 25;
00824:                     if (score > bestScore && attributes > 0)
00825:                     {
00826:                         bestScore = score;
00827:                         best = id;
00828:                         blockName = name;
00829:                     }
00830:                 }
00831:             }
00832:             return best;
00833:         }
00834: 
00835:         private static IDictionary<string, string> BuildAttributeValues(
00836:             ProductionDrawingRegisterData data,
00837:             ProductionDrawingRegisterRow row)
00838:         {
00839:             var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
00840:             {
00841:                 { "PROJECT", data.Header("Project Name") },
00842:                 { "PROJECTNAME", data.Header("Project Name") },
00843:                 { "PROJECTNO", data.Header("Project Number") },
00844:                 { "PROJECTNUMBER", data.Header("Project Number") },
00845:                 { "CLIENT", data.Header("Client") },
00846:                 { "COMPANY", data.Header("Company") },
00847:                 { "DRAWINGNO", row.DrawingNumber },
00848:                 { "DRAWINGNUMBER", row.DrawingNumber },
00849:                 { "DWGNO", row.DrawingNumber },
00850:                 { "TITLE", row.Title },
00851:                 { "DRAWINGTITLE", row.Title },
00852:                 { "SHEETTITLE", row.Title },
00853:                 { "PURPOSE", row.Purpose },
00854:                 { "DISCIPLINE", row.Purpose },
00855:                 { "SCALE", row.Scale },
00856:                 { "STAGE", row.Stage },
00857:                 { "STATUS", row.Stage },
00858:                 { "REV", row.Revision },
00859:                 { "REVISION", row.Revision },
00860:                 { "DATE", row.IssueDate },
00861:                 { "ISSUEDATE", row.IssueDate },
00862:                 { "DESIGNED", data.Header("Designed By") },
00863:                 { "DESIGNEDBY", data.Header("Designed By") },
00864:                 { "DRAWN", data.Header("Drawn By") },
00865:                 { "DRAWNBY", data.Header("Drawn By") },
00866:                 { "CHECKED", data.Header("Checked By") },
00867:                 { "CHECKEDBY", data.Header("Checked By") },
00868:                 { "APPROVED", data.Header("Approved By") },
00869:                 { "APPROVEDBY", data.Header("Approved By") },
00870:                 { "LAYOUT", row.Layout },
00871:                 { "SHEET", row.Layout }
00872:             };
00873:             return result;
00874:         }
00875: 
00876:         private static string ResolveAttributeValue(
00877:             string tag,
00878:             string fallback,
00879:             IDictionary<string, string> values)
00880:         {
00881:             string key = NormalizeTag(tag);
00882:             string value;
00883:             if (values.TryGetValue(key, out value)) return value ?? string.Empty;
00884:             foreach (KeyValuePair<string, string> pair in values)
00885:             {
00886:                 if (key.Contains(pair.Key) || pair.Key.Contains(key))
00887:                     return pair.Value ?? string.Empty;
00888:             }
00889:             return fallback ?? string.Empty;
00890:         }
00891: 
```

## ProductionReportCommands.cs
Hits: `CE_DRAWINGBOOK`, `CE_BOOKINDEX`, `DrawingRegister`, `PromptKeywordOptions`, `GetKeywords(`

### Lines 32-105
```csharp
00032:             "CE_REPORTTOOLS",
00033:             CommandFlags.Modal | CommandFlags.Redraw)]
00034:         public void ReportTools()
00035:         {
00036:             Document document = ActiveDocument();
00037:             if (document == null) return;
00038: 
00039:             DisciplineWorkflowDialogs.SelectAndRun(
00040:                 document,
00041:                 "CE Tools - Reports and Drawing Production",
00042:                 "Create model-derived reports, linked summary sheets and standard drawing-book layouts.",
00043:                 new List<DisciplineWorkflowAction>
00044:                 {
00045:                     new DisciplineWorkflowAction("Full project report", "CE_REPORTFULL", "Review all supported disciplines in the current drawing.", "01 Reports"),
00046:                     new DisciplineWorkflowAction("Discipline report", "CE_REPORTDISC", "Choose and review one engineering discipline.", "01 Reports"),
00047:                     new DisciplineWorkflowAction("Export report", "CE_REPORTEXPORT", "Export current model-derived report data.", "01 Reports"),
00048:                     new DisciplineWorkflowAction("Create summary sheet", "CE_SUMMARYSHEET", "Create a linked project summary sheet.", "02 Summary Sheet"),
00049:                     new DisciplineWorkflowAction("Refresh summary sheet", "CE_SUMMARYREFRESH", "Update an existing linked summary sheet.", "02 Summary Sheet"),
00050:                     new DisciplineWorkflowAction("Create drawing book", "CE_DRAWINGBOOK", "Create standard A0, A1, A3 or A4 layouts.", "03 Drawing Book"),
00051:                     new DisciplineWorkflowAction("Export book index", "CE_BOOKINDEX", "Export the drawing-book layout index.", "03 Drawing Book")
00052:                 });
00053:         }
00054: 
00055:         [CommandMethod("CE_TOOLS", "CE_REPORTFULL", CommandFlags.Modal | CommandFlags.Redraw)]
00056:         public void FullReport()
00057:         {
00058:             ShowReport(ActiveDocument(), ReportDiscipline.All, true);
00059:         }
00060: 
00061:         [CommandMethod("CE_TOOLS", "CE_REPORTDISC", CommandFlags.Modal | CommandFlags.Redraw)]
00062:         public void DisciplineReport()
00063:         {
00064:             Document document = ActiveDocument();
00065:             if (document == null) return;
00066:             ReportDiscipline discipline;
00067:             if (!PromptDiscipline(document.Editor, false, out discipline)) return;
00068:             ShowReport(document, discipline, true);
00069:         }
00070: 
00071:         [CommandMethod("CE_TOOLS", "CE_REPORTROAD", CommandFlags.Modal | CommandFlags.Redraw)]
00072:         public void RoadReport() { ShowReport(ActiveDocument(), ReportDiscipline.Road, true); }
00073: 
00074:         [CommandMethod("CE_TOOLS", "CE_REPORTPLATFORM", CommandFlags.Modal | CommandFlags.Redraw)]
00075:         public void PlatformReport() { ShowReport(ActiveDocument(), ReportDiscipline.Platform, true); }
00076: 
00077:         [CommandMethod("CE_TOOLS", "CE_REPORTSTORM", CommandFlags.Modal | CommandFlags.Redraw)]
00078:         public void StormReport() { ShowReport(ActiveDocument(), ReportDiscipline.Stormwater, true); }
00079: 
00080:         [CommandMethod("CE_TOOLS", "CE_REPORTSEWER", CommandFlags.Modal | CommandFlags.Redraw)]
00081:         public void SewerReport() { ShowReport(ActiveDocument(), ReportDiscipline.Sewer, true); }
00082: 
00083:         [CommandMethod("CE_TOOLS", "CE_REPORTWATER", CommandFlags.Modal | CommandFlags.Redraw)]
00084:         public void WaterReport() { ShowReport(ActiveDocument(), ReportDiscipline.Water, true); }
00085: 
00086:         [CommandMethod("CE_TOOLS", "CE_REPORTBULKWATER", CommandFlags.Modal | CommandFlags.Redraw)]
00087:         public void BulkWaterReport() { ShowReport(ActiveDocument(), ReportDiscipline.BulkWater, true); }
00088: 
00089:         [CommandMethod("CE_TOOLS", "CE_REPORTEXPORT", CommandFlags.Modal | CommandFlags.Redraw)]
00090:         public void ExportReport()
00091:         {
00092:             Document document = ActiveDocument();
00093:             if (document == null) return;
00094:             ReportDiscipline discipline;
00095:             if (!PromptDiscipline(document.Editor, true, out discipline)) return;
00096: 
00097:             ProjectSnapshot snapshot = BuildSnapshot(document.Database, discipline);
00098:             string defaultName = "CE-Tools-" + discipline + "-Design-Report.xlsx";
00099:             string path;
00100:             if (!PromptExcelPath(document.Editor, defaultName, out path)) return;
00101: 
00102:             try
00103:             {
00104:                 SimpleXlsxWriter.Write(
00105:                     path,
```

### Lines 247-466
```csharp
00247:                     new List<string> { "Refresh model", "Explicit CE_SUMMARYREFRESH" }
00248:                 };
00249:                 GridReportPresenter.ShowReportAndOfferTable(
00250:                     document,
00251:                     "CE Tools Project Summary Link",
00252:                     "The summary is linked to current drawing contents through an explicit refresh command.",
00253:                     new List<string> { "Property", "Value" },
00254:                     rows,
00255:                     "CE TOOLS SUMMARY LINK");
00256:             }
00257:             catch (System.Exception exception)
00258:             {
00259:                 document.Editor.WriteMessage(
00260:                     "\nCE_SUMMARYINFO cancelled. {0}",
00261:                     exception.Message);
00262:             }
00263:         }
00264: 
00265:         [CommandMethod("CE_TOOLS", "CE_DRAWINGBOOK", CommandFlags.Modal | CommandFlags.Redraw)]
00266:         public void CreateDrawingBook()
00267:         {
00268:             Document document = ActiveDocument();
00269:             if (document == null) return;
00270: 
00271:             ProjectSnapshot snapshot = BuildSnapshot(
00272:                 document.Database,
00273:                 ReportDiscipline.All);
00274:             List<BookPackage> packages = StandardBookPackages();
00275:             var seeds = packages.Select(package => new ProductionDrawingSeed(
00276:                 package.LayoutName,
00277:                 package.Purpose,
00278:                 package.Purpose,
00279:                 package.PaperName,
00280:                 "As shown")).ToList();
00281:             foreach (LayoutSnapshot layout in snapshot.Layouts)
00282:             {
00283:                 if (seeds.Any(seed => string.Equals(
00284:                         seed.Layout,
00285:                         layout.Name,
00286:                         StringComparison.OrdinalIgnoreCase)))
00287:                     continue;
00288:                 seeds.Add(new ProductionDrawingSeed(
00289:                     layout.Name,
00290:                     layout.Name,
00291:                     "Project drawing",
00292:                     "Existing",
00293:                     "As shown"));
00294:             }
00295: 
00296:             ProductionDrawingRegisterData drawingRegister;
00297:             if (!ProductionDrawingRegisterCommands.EditForProduction(
00298:                     document,
00299:                     seeds,
00300:                     "Save & Generate",
00301:                     out drawingRegister))
00302:                 return;
00303: 
00304:             try
00305:             {
00306:                 int created = 0;
00307:                 int refreshed = 0;
00308:                 foreach (BookPackage package in packages)
00309:                 {
00310:                     bool wasCreated = CreateOrRefreshBookLayout(
00311:                         document.Database,
00312:                         package,
00313:                         snapshot,
00314:                         drawingRegister);
00315:                     if (wasCreated) created++;
00316:                     else refreshed++;
00317:                 }
00318:                 document.Editor.WriteMessage(
00319:                     "
00320: CE_DRAWINGBOOK complete. Layouts created={0}; refreshed={1}. Titles, title blocks and the drawing register use the saved popup values.",
00321:                     created,
00322:                     refreshed);
00323:             }
00324:             catch (System.Exception exception)
00325:             {
00326:                 document.Editor.WriteMessage(
00327:                     "
00328: CE_DRAWINGBOOK failed. {0}",
00329:                     exception.Message);
00330:             }
00331:         }
00332: 
00333:         [CommandMethod("CE_TOOLS", "CE_BOOKINDEX", CommandFlags.Modal | CommandFlags.Redraw)]
00334:         public void ExportDrawingBookIndex()
00335:         {
00336:             Document document = ActiveDocument();
00337:             if (document == null) return;
00338:             ProjectSnapshot snapshot = BuildSnapshot(
00339:                 document.Database,
00340:                 ReportDiscipline.All);
00341:             var seeds = StandardBookPackages()
00342:                 .Select(package => new ProductionDrawingSeed(
00343:                     package.LayoutName,
00344:                     package.Purpose,
00345:                     package.Purpose,
00346:                     package.PaperName,
00347:                     "As shown"))
00348:                 .ToList();
00349:             foreach (LayoutSnapshot layout in snapshot.Layouts)
00350:                 seeds.Add(new ProductionDrawingSeed(
00351:                     layout.Name,
00352:                     layout.Name,
00353:                     "Project drawing",
00354:                     "Existing",
00355:                     "As shown"));
00356: 
00357:             ProductionDrawingRegisterData register;
00358:             if (!ProductionDrawingRegisterCommands.EditForProduction(
00359:                     document,
00360:                     seeds,
00361:                     "Save & Export Index",
00362:                     out register))
00363:                 return;
00364: 
00365:             string path;
00366:             if (!PromptExcelPath(
00367:                 document.Editor,
00368:                 "CE-Tools-Drawing-Book-Index.xlsx",
00369:                 out path)) return;
00370:             var rows = new List<IList<string>>
00371:             {
00372:                 new List<string>
00373:                 {
00374:                     "CE TOOLS DRAWING BOOK INDEX", string.Empty, string.Empty,
00375:                     string.Empty, string.Empty, string.Empty, string.Empty,
00376:                     string.Empty, string.Empty
00377:                 },
00378:                 new List<string>
00379:                 {
00380:                     "DRAWING NO.", "LAYOUT", "TITLE", "PURPOSE / DISCIPLINE",
00381:                     "PAPER", "SCALE", "STAGE", "REVISION", "ISSUE DATE"
00382:                 }
00383:             };
00384:             foreach (ProductionDrawingRegisterRow row in register.Rows)
00385:             {
00386:                 rows.Add(new List<string>
00387:                 {
00388:                     row.DrawingNumber,
00389:                     row.Layout,
00390:                     row.Title,
00391:                     row.Purpose,
00392:                     row.Paper,
00393:                     row.Scale,
00394:                     row.Stage,
00395:                     row.Revision,
00396:                     row.IssueDate
00397:                 });
00398:             }
00399:             try
00400:             {
00401:                 SimpleXlsxWriter.Write(path, "Drawing Book Index", rows);
00402:                 document.Editor.WriteMessage(
00403:                     "
00404: CE_BOOKINDEX complete. Drawings listed={0}; workbook={1}",
00405:                     register.Rows.Count,
00406:                     path);
00407:             }
00408:             catch (System.Exception exception)
00409:             {
00410:                 document.Editor.WriteMessage(
00411:                     "
00412: CE_BOOKINDEX failed. {0}",
00413:                     exception.Message);
00414:             }
00415:         }
00416: 
00417:         private static void ShowReport(
00418:             Document document,
00419:             ReportDiscipline discipline,
00420:             bool offerTable)
00421:         {
00422:             if (document == null) return;
00423:             ProjectSnapshot snapshot = BuildSnapshot(document.Database, discipline);
00424:             WriteSnapshotPreview(document.Editor, snapshot);
00425: 
00426:             var columns = new List<string>
00427:             {
00428:                 "Discipline", "Layer", "Object Type", "Count",
00429:                 "Length", "Area", "Volume", "Status / Detail"
00430:             };
00431:             var rows = new List<IList<string>>();
00432:             foreach (ReportGroup group in snapshot.Groups)
00433:             {
00434:                 rows.Add(new List<string>
00435:                 {
00436:                     group.Discipline.ToString(),
00437:                     group.Layer,
00438:                     group.TypeName,
00439:                     group.Count.ToString(CultureInfo.InvariantCulture),
00440:                     group.Length > 0.0
00441:                         ? group.Length.ToString("N3", CultureInfo.CurrentCulture)
00442:                         : string.Empty,
00443:                     group.Area > 0.0
00444:                         ? group.Area.ToString("N3", CultureInfo.CurrentCulture)
00445:                         : string.Empty,
00446:                     group.Volume > 0.0
00447:                         ? group.Volume.ToString("N3", CultureInfo.CurrentCulture)
00448:                         : string.Empty,
00449:                     group.Detail
00450:                 });
00451:             }
00452: 
00453:             if (rows.Count == 0)
00454:             {
00455:                 rows.Add(new List<string>
00456:                 {
00457:                     discipline.ToString(), string.Empty, string.Empty, "0",
00458:                     string.Empty, string.Empty, string.Empty,
00459:                     "No matching model-space design objects"
00460:                 });
00461:             }
00462: 
00463:             GridReportPresenter.ShowReportAndOfferTable(
00464:                 document,
00465:                 "CE Tools " + discipline + " Design Report",
00466:                 BuildReportNote(snapshot),
```

### Lines 870-990
```csharp
00870:             entity.CreateExtensionDictionary();
00871:             DBDictionary dictionary = transaction.GetObject(
00872:                 entity.ExtensionDictionary,
00873:                 OpenMode.ForWrite,
00874:                 false) as DBDictionary;
00875:             Xrecord record = OpenOrCreateRecord(
00876:                 dictionary,
00877:                 SummaryGeneratedRecordName,
00878:                 transaction);
00879:             record.Data = new ResultBuffer(
00880:                 new TypedValue((int)DxfCode.Text, "Anchor=" + anchorHandle));
00881:             handles.Add(entity.Handle.ToString());
00882:         }
00883: 
00884:         private static bool CreateOrRefreshBookLayout(
00885:             Database database,
00886:             BookPackage package,
00887:             ProjectSnapshot snapshot,
00888:             ProductionDrawingRegisterData drawingRegister)
00889:         {
00890:             bool created = false;
00891:             ObjectId layoutId = FindLayoutId(database, package.LayoutName);
00892:             if (layoutId.IsNull)
00893:             {
00894:                 layoutId = LayoutManager.Current.CreateLayout(package.LayoutName);
00895:                 created = true;
00896:             }
00897: 
00898:             using (Transaction transaction = database.TransactionManager.StartTransaction())
00899:             {
00900:                 Layout layout = transaction.GetObject(
00901:                     layoutId,
00902:                     OpenMode.ForWrite,
00903:                     false) as Layout;
00904:                 if (layout == null)
00905:                     throw new InvalidOperationException(
00906:                         "Layout could not be opened: " + package.LayoutName);
00907: 
00908:                 BookLink oldLink = ReadBookLinkIfPresent(layout, transaction);
00909:                 if (oldLink != null)
00910:                 {
00911:                     foreach (string handle in oldLink.GeneratedHandles)
00912:                     {
00913:                         ObjectId id;
00914:                         if (!TryResolveHandle(database, handle, out id)) continue;
00915:                         Entity old = transaction.GetObject(
00916:                             id,
00917:                             OpenMode.ForWrite,
00918:                             false) as Entity;
00919:                         if (old != null && !old.IsErased) old.Erase();
00920:                     }
00921:                 }
00922: 
00923:                 BlockTableRecord paperSpace = transaction.GetObject(
00924:                     layout.BlockTableRecordId,
00925:                     OpenMode.ForWrite,
00926:                     false) as BlockTableRecord;
00927:                 if (paperSpace == null)
00928:                     throw new InvalidOperationException(
00929:                         "Paper space could not be opened for " + package.LayoutName);
00930: 
00931:                 double margin = package.Width >= 800.0 ? 20.0 : 10.0;
00932:                 double titleHeight = package.Width >= 800.0 ? 7.0 : 4.0;
00933:                 var generated = new List<string>();
00934:                 ProductionDrawingRegisterRow registerRow =
00935:                     drawingRegister.Find(package.LayoutName) ??
00936:                     new ProductionDrawingRegisterRow
00937:                     {
00938:                         DrawingNumber = package.LayoutName,
00939:                         Layout = package.LayoutName,
00940:                         Title = package.Purpose,
00941:                         Purpose = package.Purpose,
00942:                         Paper = package.PaperName,
00943:                         Scale = "As shown",
00944:                         Stage = drawingRegister.Header("Project Stage"),
00945:                         Revision = drawingRegister.Header("Revision"),
00946:                         IssueDate = drawingRegister.Header("Issue Date")
00947:                     };
00948: 
00949:                 string titleBlockDiagnostic;
00950:                 ObjectId titleBlockId = ProductionTitleBlockManager.TryInsert(
00951:                     database,
00952:                     transaction,
00953:                     paperSpace,
00954:                     drawingRegister.Header("Title Block Source"),
00955:                     package.PaperName,
00956:                     Point3d.Origin,
00957:                     drawingRegister,
00958:                     registerRow,
00959:                     out titleBlockDiagnostic);
00960:                 if (!titleBlockId.IsNull)
00961:                     generated.Add(titleBlockId.Handle.ToString());
00962: 
00963:                 var frame = new Polyline();
00964:                 frame.SetDatabaseDefaults(database);
00965:                 frame.AddVertexAt(0, new Point2d(margin, margin), 0.0, 0.0, 0.0);
00966:                 frame.AddVertexAt(1, new Point2d(package.Width - margin, margin), 0.0, 0.0, 0.0);
00967:                 frame.AddVertexAt(2, new Point2d(package.Width - margin, package.Height - margin), 0.0, 0.0, 0.0);
00968:                 frame.AddVertexAt(3, new Point2d(margin, package.Height - margin), 0.0, 0.0, 0.0);
00969:                 frame.Closed = true;
00970:                 AddBookGenerated(transaction, paperSpace, frame, package.LayoutName, generated);
00971: 
00972:                 var title = new MText();
00973:                 title.SetDatabaseDefaults(database);
00974:                 title.Location = new Point3d(
00975:                     margin * 1.5,
00976:                     package.Height - margin * 1.8,
00977:                     0.0);
00978:                 title.TextHeight = titleHeight;
00979:                 title.Width = package.Width - margin * 3.0;
00980:                 title.Contents = string.Join(
00981:                     "\\P",
00982:                     registerRow.DrawingNumber + "  |  " + registerRow.Title.ToUpperInvariant(),
00983:                     ValueOrNotSet(drawingRegister.Header("Project Name")) +
00984:                         "  |  " + ValueOrNotSet(drawingRegister.Header("Client")),
00985:                     registerRow.Paper + " | Scale " + registerRow.Scale +
00986:                         " | Stage " + registerRow.Stage +
00987:                         " | Rev " + registerRow.Revision +
00988:                         " | " + registerRow.IssueDate);
00989:                 AddBookGenerated(transaction, paperSpace, title, package.LayoutName, generated);
00990: 
```

### Lines 1017-1130
```csharp
01017:                     new BookLink(
01018:                         SchemaVersion,
01019:                         package.LayoutName,
01020:                         package.PaperName,
01021:                         package.Purpose,
01022:                         package.Width,
01023:                         package.Height,
01024:                         generated));
01025:                 transaction.Commit();
01026:             }
01027:             return created;
01028:         }
01029: 
01030:         private static Table BuildBookRegister(
01031:             Database database,
01032:             Point3d position,
01033:             BookPackage package,
01034:             ProjectSnapshot snapshot,
01035:             ProductionDrawingRegisterData drawingRegister,
01036:             double textHeight)
01037:         {
01038:             List<ProductionDrawingRegisterRow> rows = drawingRegister.Rows
01039:                 .Take(package.PaperName == "A4" ? 10 : 24)
01040:                 .ToList();
01041:             if (rows.Count == 0)
01042:             {
01043:                 rows.Add(new ProductionDrawingRegisterRow
01044:                 {
01045:                     DrawingNumber = "-",
01046:                     Layout = package.LayoutName,
01047:                     Title = "No drawings registered",
01048:                     Purpose = package.Purpose,
01049:                     Revision = drawingRegister.Header("Revision")
01050:                 });
01051:             }
01052: 
01053:             var table = new Table();
01054:             table.SetDatabaseDefaults(database);
01055:             table.TableStyle = database.Tablestyle;
01056:             table.Position = position;
01057:             table.SetSize(rows.Count + 2, 5);
01058:             table.SetRowHeight(textHeight * 2.0);
01059:             double available = package.Width * 0.82;
01060:             table.Columns[0].Width = available * 0.14;
01061:             table.Columns[1].Width = available * 0.24;
01062:             table.Columns[2].Width = available * 0.38;
01063:             table.Columns[3].Width = available * 0.12;
01064:             table.Columns[4].Width = available * 0.12;
01065:             table.MergeCells(CellRange.Create(table, 0, 0, 0, 4));
01066:             table.Cells[0, 0].TextString = "DRAWING BOOK REGISTER";
01067:             string[] headings =
01068:             {
01069:                 "DRAWING NO.", "LAYOUT", "TITLE", "SCALE", "REV"
01070:             };
01071:             for (int column = 0; column < headings.Length; column++)
01072:                 table.Cells[1, column].TextString = headings[column];
01073:             for (int index = 0; index < rows.Count; index++)
01074:             {
01075:                 int rowIndex = index + 2;
01076:                 ProductionDrawingRegisterRow item = rows[index];
01077:                 table.Cells[rowIndex, 0].TextString = item.DrawingNumber;
01078:                 table.Cells[rowIndex, 1].TextString = item.Layout;
01079:                 table.Cells[rowIndex, 2].TextString = item.Title;
01080:                 table.Cells[rowIndex, 3].TextString = item.Scale;
01081:                 table.Cells[rowIndex, 4].TextString = item.Revision;
01082:             }
01083:             for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
01084:                 for (int column = 0; column < table.Columns.Count; column++)
01085:                     table.Cells[rowIndex, column].TextHeight = textHeight;
01086:             return table;
01087:         }
01088: 
01089:         private static void AddBookGenerated(
01090:             Transaction transaction,
01091:             BlockTableRecord paperSpace,
01092:             Entity entity,
01093:             string layoutName,
01094:             ICollection<string> handles)
01095:         {
01096:             paperSpace.AppendEntity(entity);
01097:             transaction.AddNewlyCreatedDBObject(entity, true);
01098:             entity.CreateExtensionDictionary();
01099:             DBDictionary dictionary = transaction.GetObject(
01100:                 entity.ExtensionDictionary,
01101:                 OpenMode.ForWrite,
01102:                 false) as DBDictionary;
01103:             Xrecord record = OpenOrCreateRecord(
01104:                 dictionary,
01105:                 BookRecordName,
01106:                 transaction);
01107:             record.Data = new ResultBuffer(
01108:                 new TypedValue((int)DxfCode.Text, "Layout=" + layoutName));
01109:             handles.Add(entity.Handle.ToString());
01110:         }
01111: 
01112:         private static void WriteSummaryLink(
01113:             Entity anchor,
01114:             Transaction transaction,
01115:             SummaryLink link)
01116:         {
01117:             DBDictionary dictionary = transaction.GetObject(
01118:                 anchor.ExtensionDictionary,
01119:                 OpenMode.ForWrite,
01120:                 false) as DBDictionary;
01121:             Xrecord record = OpenOrCreateRecord(
01122:                 dictionary,
01123:                 SummaryRecordName,
01124:                 transaction);
01125:             var values = new List<TypedValue>
01126:             {
01127:                 new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion),
01128:                 new TypedValue((int)DxfCode.Text, "Anchor=" + link.AnchorHandle),
01129:                 new TypedValue((int)DxfCode.Text, "InsertionX=" + link.InsertionPoint.X.ToString("R", CultureInfo.InvariantCulture)),
01130:                 new TypedValue((int)DxfCode.Text, "InsertionY=" + link.InsertionPoint.Y.ToString("R", CultureInfo.InvariantCulture)),
```

### Lines 1643-1773
```csharp
01643:                     map.Add(group.Discipline, summary);
01644:                 }
01645:                 summary.Count += group.Count;
01646:                 summary.Length += group.Length;
01647:                 summary.Area += group.Area;
01648:                 summary.Volume += group.Volume;
01649:             }
01650:             if (map.Count == 0)
01651:                 map.Add(ReportDiscipline.General, new DisciplineSummary(ReportDiscipline.General));
01652:             return map.Values.ToList();
01653:         }
01654: 
01655:         private static bool PromptDiscipline(
01656:             Editor editor,
01657:             bool includeAll,
01658:             out ReportDiscipline discipline)
01659:         {
01660:             string allText = includeAll ? "All/" : string.Empty;
01661:             var options = new PromptKeywordOptions(
01662:                 "\nReport discipline [" + allText +
01663:                 "General/Road/Platform/Stormwater/Sewer/Water/BulkWater] <" +
01664:                 (includeAll ? "All" : "General") + ">: ")
01665:             {
01666:                 AllowNone = true
01667:             };
01668:             if (includeAll) options.Keywords.Add("All");
01669:             foreach (string keyword in new[]
01670:             {
01671:                 "General", "Road", "Platform", "Stormwater", "Sewer", "Water", "BulkWater"
01672:             })
01673:                 options.Keywords.Add(keyword);
01674:             PromptResult result = editor.GetKeywords(options);
01675:             if (result.Status == PromptStatus.Cancel)
01676:             {
01677:                 discipline = ReportDiscipline.General;
01678:                 return false;
01679:             }
01680:             string selected = result.Status == PromptStatus.None
01681:                 ? (includeAll ? "All" : "General")
01682:                 : result.StringResult;
01683:             return Enum.TryParse(selected, true, out discipline);
01684:         }
01685: 
01686:         private static bool PromptExcelPath(
01687:             Editor editor,
01688:             string defaultName,
01689:             out string path)
01690:         {
01691:             var options = new PromptSaveFileOptions(
01692:                 "\nSelect Excel workbook output path: ")
01693:             {
01694:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
01695:                 DialogCaption = "Export CE Tools Design Report",
01696:                 InitialFileName = defaultName
01697:             };
01698:             PromptFileNameResult result = editor.GetFileNameForSave(options);
01699:             if (result.Status != PromptStatus.OK)
01700:             {
01701:                 path = string.Empty;
01702:                 return false;
01703:             }
01704:             path = result.StringResult;
01705:             if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
01706:                 path += ".xlsx";
01707:             return true;
01708:         }
01709: 
01710:         private static bool Confirm(Editor editor, string message)
01711:         {
01712:             var options = new PromptKeywordOptions(
01713:                 "\n" + message + "? [Yes/No] <No>: ")
01714:             {
01715:                 AllowNone = true
01716:             };
01717:             options.Keywords.Add("Yes");
01718:             options.Keywords.Add("No");
01719:             PromptResult result = editor.GetKeywords(options);
01720:             return result.Status == PromptStatus.OK && Equal(result.StringResult, "Yes");
01721:         }
01722: 
01723:         private static ObjectId FindLayoutId(Database database, string layoutName)
01724:         {
01725:             using (Transaction transaction = database.TransactionManager.StartTransaction())
01726:             {
01727:                 DBDictionary layouts = transaction.GetObject(
01728:                     database.LayoutDictionaryId,
01729:                     OpenMode.ForRead,
01730:                     false) as DBDictionary;
01731:                 if (layouts != null && layouts.Contains(layoutName))
01732:                     return layouts.GetAt(layoutName);
01733:             }
01734:             return ObjectId.Null;
01735:         }
01736: 
01737:         private static bool HasRecord(
01738:             DBObject value,
01739:             Transaction transaction,
01740:             string recordName)
01741:         {
01742:             if (value == null || value.ExtensionDictionary.IsNull) return false;
01743:             DBDictionary dictionary = transaction.GetObject(
01744:                 value.ExtensionDictionary,
01745:                 OpenMode.ForRead,
01746:                 false) as DBDictionary;
01747:             return dictionary != null && dictionary.Contains(recordName);
01748:         }
01749: 
01750:         private static Xrecord OpenOrCreateRecord(
01751:             DBDictionary dictionary,
01752:             string name,
01753:             Transaction transaction)
01754:         {
01755:             if (dictionary == null)
01756:                 throw new InvalidOperationException("The CE Tools extension dictionary is unavailable.");
01757:             if (dictionary.Contains(name))
01758:                 return transaction.GetObject(
01759:                     dictionary.GetAt(name),
01760:                     OpenMode.ForWrite,
01761:                     false) as Xrecord;
01762:             var record = new Xrecord();
01763:             dictionary.SetAt(name, record);
01764:             transaction.AddNewlyCreatedDBObject(record, true);
01765:             return record;
01766:         }
01767: 
01768:         private static bool TryResolveHandle(
01769:             Database database,
01770:             string handleText,
01771:             out ObjectId objectId)
01772:         {
01773:             return DynamicCrossSectionCommands.TryResolveHandle(
```

## ProfileViewBatchCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 392-471
```csharp
00392:                     null,
00393:                     new[] { typeof(ObjectId) },
00394:                     null);
00395:                 if (method == null) return false;
00396:                 method.Invoke(value, new object[] { objectId });
00397:                 return true;
00398:             }
00399:             catch
00400:             {
00401:                 return false;
00402:             }
00403:         }
00404: 
00405:         private static List<ProfileViewItem> PromptScope(
00406:             Document document,
00407:             IList<ProfileViewItem> all)
00408:         {
00409:             if (all == null || all.Count == 0) return new List<ProfileViewItem>();
00410:             var options = new PromptKeywordOptions(
00411:                 "\nProfile view scope [All/Select] <All>: ")
00412:             {
00413:                 AllowNone = true
00414:             };
00415:             options.Keywords.Add("All");
00416:             options.Keywords.Add("Select");
00417:             PromptResult result = document.Editor.GetKeywords(options);
00418:             if (result.Status == PromptStatus.Cancel) return new List<ProfileViewItem>();
00419:             if (result.Status != PromptStatus.OK ||
00420:                 string.Equals(result.StringResult, "All", StringComparison.OrdinalIgnoreCase))
00421:                 return all.ToList();
00422: 
00423:             PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
00424:             {
00425:                 MessageForAdding = "\nSelect Civil 3D profile views: ",
00426:                 AllowDuplicates = false,
00427:                 RejectObjectsFromNonCurrentSpace = true
00428:             });
00429:             if (selection.Status != PromptStatus.OK) return new List<ProfileViewItem>();
00430:             var selectedIds = new HashSet<ObjectId>(selection.Value.GetObjectIds());
00431:             return all.Where(item => selectedIds.Contains(item.ObjectId)).ToList();
00432:         }
00433: 
00434:         private static List<ProfileViewItem> ReadProfileViews(Document document)
00435:         {
00436:             var result = new List<ProfileViewItem>();
00437:             CivilDocument civilDocument = CivilApplication.ActiveDocument;
00438:             if (civilDocument == null) return result;
00439:             using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00440:             {
00441:                 foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
00442:                 {
00443:                     DBObject alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false);
00444:                     string alignmentName = ReadStringProperty(alignment, "Name");
00445:                     foreach (ObjectId viewId in ReadObjectIds(alignment, "GetProfileViewIds"))
00446:                     {
00447:                         DBObject view = transaction.GetObject(viewId, OpenMode.ForRead, false);
00448:                         if (view == null) continue;
00449:                         result.Add(new ProfileViewItem(
00450:                             viewId,
00451:                             ReadStringProperty(view, "Name"),
00452:                             alignmentName,
00453:                             ReadStringProperty(view, "StyleName"),
00454:                             FirstNonBlank(
00455:                                 ReadStringProperty(view, "BandSetStyleName"),
00456:                                 ReadNestedString(view, "Bands", "BandSetStyleName")),
00457:                             FormatRange(view, "StationStart", "StationEnd"),
00458:                             FormatRange(view, "ElevationMin", "ElevationMax"),
00459:                             ReadBoolProperty(view, "IsOutOfDate") ? "Out of date" : "Current"));
00460:                     }
00461:                 }
00462:             }
00463:             return result
00464:                 .OrderBy(item => item.AlignmentName, StringComparer.CurrentCultureIgnoreCase)
00465:                 .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
00466:                 .ToList();
00467:         }
00468: 
00469:         private static ProfileViewStyleCatalogue ReadStyleCatalogue(Document document)
00470:         {
00471:             var catalogue = new ProfileViewStyleCatalogue();
```

## ProjectPresentationCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 16-95
```csharp
00016: 
00017: namespace CETools.Civil3D
00018: {
00019:     /// <summary>
00020:     /// Generates a dependency-free project review PowerPoint from the current
00021:     /// drawing inventory and automated model-health checks. The presentation is
00022:     /// a review aid and does not replace drawing, design or engineering approval.
00023:     /// </summary>
00024:     public sealed class ProjectPresentationCommands
00025:     {
00026:         private const int MaximumInventoryTypes = 15;
00027:         private const int MaximumFindingsPerSlide = 9;
00028: 
00029:         [CommandMethod("CE_TOOLS", "CE_PROJECTPRESENTATIONTOOLS", CommandFlags.Modal)]
00030:         public void PresentationTools()
00031:         {
00032:             Document document = ActiveDocument();
00033:             if (document == null) return;
00034:             var options = new PromptKeywordOptions(
00035:                 "\nProject presentation tools [Preview/Create] <Create>: ")
00036:             {
00037:                 AllowNone = true
00038:             };
00039:             options.Keywords.Add("Preview");
00040:             options.Keywords.Add("Create");
00041:             PromptResult result = document.Editor.GetKeywords(options);
00042:             if (result.Status == PromptStatus.Cancel) return;
00043:             string command = result.Status == PromptStatus.OK &&
00044:                 string.Equals(result.StringResult, "Preview", StringComparison.OrdinalIgnoreCase)
00045:                 ? "CE_PRESENTATIONPREVIEW "
00046:                 : "CE_PRESENTATIONCREATE ";
00047:             document.SendStringToExecute(command, true, false, true);
00048:         }
00049: 
00050:         [CommandMethod("CE_TOOLS", "CE_PRESENTATIONPREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
00051:         public void PreviewPresentation()
00052:         {
00053:             Document document = ActiveDocument();
00054:             if (document == null) return;
00055:             PresentationProjectInput input;
00056:             if (!PromptProjectInput(document.Editor, document.Database, out input)) return;
00057:             try
00058:             {
00059:                 DrawingPresentationSnapshot snapshot = ReadSnapshot(document.Database);
00060:                 PresentationDeck deck = BuildDeck(input, snapshot);
00061:                 ShowPreview(document, deck, snapshot);
00062:             }
00063:             catch (System.Exception exception)
00064:             {
00065:                 document.Editor.WriteMessage("\nCE_PRESENTATIONPREVIEW failed. {0}", exception.Message);
00066:             }
00067:         }
00068: 
00069:         [CommandMethod("CE_TOOLS", "CE_PRESENTATIONCREATE", CommandFlags.Modal | CommandFlags.Redraw)]
00070:         public void CreatePresentation()
00071:         {
00072:             Document document = ActiveDocument();
00073:             if (document == null) return;
00074:             PresentationProjectInput input;
00075:             if (!PromptProjectInput(document.Editor, document.Database, out input)) return;
00076: 
00077:             var saveOptions = new PromptSaveFileOptions(
00078:                 "\nChoose the project presentation path: ")
00079:             {
00080:                 Filter = "PowerPoint Presentation (*.pptx)|*.pptx",
00081:                 DialogCaption = "Create CE Tools Project Presentation",
00082:                 InitialFileName = SafeFileName(input.ProjectTitle) + "-Project-Review.pptx"
00083:             };
00084:             PromptFileNameResult saveResult = document.Editor.GetFileNameForSave(saveOptions);
00085:             if (saveResult.Status != PromptStatus.OK) return;
00086:             string path = saveResult.StringResult.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
00087:                 ? saveResult.StringResult
00088:                 : saveResult.StringResult + ".pptx";
00089:             if (File.Exists(path))
00090:             {
00091:                 document.Editor.WriteMessage(
00092:                     "\nCE_PRESENTATIONCREATE stopped. Existing presentation files are not overwritten.");
00093:                 return;
00094:             }
00095: 
```

### Lines 504-596
```csharp
00504:                 !PromptText(editor, "Project stage", "Design Review", out stage) ||
00505:                 !PromptText(editor, "Presentation purpose", "Civil 3D project review", out purpose) ||
00506:                 !PromptText(editor, "Prepared by", Environment.UserName, out author) ||
00507:                 !PromptText(editor, "Company", "CE Tools", out company))
00508:             {
00509:                 input = null;
00510:                 return false;
00511:             }
00512:             input = new PresentationProjectInput(title, client, stage, purpose, author, company);
00513:             return true;
00514:         }
00515: 
00516:         private static bool PromptText(
00517:             Editor editor,
00518:             string label,
00519:             string defaultValue,
00520:             out string value)
00521:         {
00522:             var options = new PromptStringOptions("\n" + label + " <" + defaultValue + ">: ")
00523:             {
00524:                 AllowSpaces = true,
00525:                 UseDefaultValue = true,
00526:                 DefaultValue = defaultValue
00527:             };
00528:             PromptResult result = editor.GetString(options);
00529:             value = result.Status == PromptStatus.OK ? result.StringResult : defaultValue;
00530:             return result.Status != PromptStatus.Cancel && !string.IsNullOrWhiteSpace(value);
00531:         }
00532: 
00533:         private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
00534:         {
00535:             var options = new PromptKeywordOptions(
00536:                 "\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00537:             {
00538:                 AllowNone = true
00539:             };
00540:             options.Keywords.Add("Yes");
00541:             options.Keywords.Add("No");
00542:             PromptResult result = editor.GetKeywords(options);
00543:             if (result.Status == PromptStatus.Cancel) return false;
00544:             return result.Status == PromptStatus.None
00545:                 ? defaultValue
00546:                 : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00547:         }
00548: 
00549:         private static string ReadCoordinateSystemCode()
00550:         {
00551:             try
00552:             {
00553:                 CivilDocument civil = CivilApplication.ActiveDocument;
00554:                 if (civil == null) return string.Empty;
00555:                 object settings = ReflectionValue(civil, "Settings");
00556:                 object ambient = ReflectionValue(settings, "DrawingSettings");
00557:                 object code = ReflectionValue(ambient, "CoordinateSystemCode");
00558:                 return Convert.ToString(code, CultureInfo.CurrentCulture);
00559:             }
00560:             catch { return string.Empty; }
00561:         }
00562: 
00563:         private static int ParseInt(string value)
00564:         {
00565:             int result;
00566:             return int.TryParse(
00567:                 (value ?? string.Empty).Replace(",", string.Empty).Replace(" ", string.Empty),
00568:                 NumberStyles.Integer,
00569:                 CultureInfo.CurrentCulture,
00570:                 out result)
00571:                 ? result
00572:                 : 0;
00573:         }
00574: 
00575:         private static string FormatExtents(Point3d minimum, Point3d maximum)
00576:         {
00577:             return string.Format(
00578:                 CultureInfo.CurrentCulture,
00579:                 "X {0:N3} to {1:N3}; Y {2:N3} to {3:N3}; Z {4:N3} to {5:N3}",
00580:                 minimum.X, maximum.X, minimum.Y, maximum.Y, minimum.Z, maximum.Z);
00581:         }
00582: 
00583:         private static string FriendlyType(string type)
00584:         {
00585:             return (type ?? string.Empty)
00586:                 .Replace("Polyline", "Polyline")
00587:                 .Replace("BlockReference", "Block references")
00588:                 .Replace("DBText", "Text")
00589:                 .Replace("MText", "MText")
00590:                 .Replace("MLeader", "MLeaders");
00591:         }
00592: 
00593:         private static string SafeFileName(string value)
00594:         {
00595:             string result = value ?? "CE-Tools-Project";
00596:             foreach (char invalid in Path.GetInvalidFileNameChars())
```

## ProjectSetupCommands.cs
Hits: `CE_PROJECTSETUP`, `PromptStringOptions`, `GetString(`, `ProjectSetupPopupWindow`

### Lines 48-241
```csharp
00048:         [CommandMethod(
00049:             "CE_TOOLS",
00050:             "CE_PROJECT",
00051:             CommandFlags.Modal | CommandFlags.Redraw)]
00052:         public void ProjectMenu()
00053:         {
00054:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00055:             if (document == null)
00056:             {
00057:                 return;
00058:             }
00059: 
00060:             DisciplineWorkflowDialogs.SelectAndRun(
00061:                 document,
00062:                 "CE Tools - Project Setup",
00063:                 "Create, inspect and safely maintain drawing-embedded CE Tools project metadata.",
00064:                 new List<DisciplineWorkflowAction>
00065:                 {
00066:                     new DisciplineWorkflowAction("Set up project", "CE_PROJECTSETUP", "Enter project, client, location, standards, template and units.", "01 Project"),
00067:                     new DisciplineWorkflowAction("Project information", "CE_PROJECTINFO", "Review stored project metadata and optionally place a table.", "01 Project"),
00068:                     new DisciplineWorkflowAction("Clear project metadata", "CE_PROJECTCLEAR", "Back up and clear the current project metadata.", "02 Recovery"),
00069:                     new DisciplineWorkflowAction("Restore project metadata", "CE_PROJECTRESTORE", "Restore the latest CE Tools metadata backup.", "02 Recovery")
00070:                 });
00071:         }
00072: 
00073:         [CommandMethod(
00074:             "CE_TOOLS",
00075:             "CE_PROJECTSETUP",
00076:             CommandFlags.Modal | CommandFlags.Redraw)]
00077:         public void ProjectSetup()
00078:         {
00079:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00080:             if (document != null)
00081:             {
00082:                 SetupProject(document);
00083:             }
00084:         }
00085: 
00086:         [CommandMethod(
00087:             "CE_TOOLS",
00088:             "CE_PROJECTINFO",
00089:             CommandFlags.Modal | CommandFlags.Redraw)]
00090:         public void ProjectInfo()
00091:         {
00092:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00093:             if (document != null)
00094:             {
00095:                 ReportProjectInfo(document);
00096:             }
00097:         }
00098: 
00099:         [CommandMethod(
00100:             "CE_TOOLS",
00101:             "CE_PROJECTCLEAR",
00102:             CommandFlags.Modal | CommandFlags.Redraw)]
00103:         public void ProjectClear()
00104:         {
00105:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00106:             if (document != null)
00107:             {
00108:                 ClearProjectInfo(document);
00109:             }
00110:         }
00111: 
00112:         [CommandMethod(
00113:             "CE_TOOLS",
00114:             "CE_PROJECTRESTORE",
00115:             CommandFlags.Modal | CommandFlags.Redraw)]
00116:         public void ProjectRestore()
00117:         {
00118:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00119:             if (document != null)
00120:             {
00121:                 RestoreProjectInfo(document);
00122:             }
00123:         }
00124: 
00125:         private static void SetupProject(Document document)
00126:         {
00127:             Editor editor = document.Editor;
00128:             ProjectMetadata existing = ReadProjectMetadata(
00129:                 document.Database,
00130:                 ProjectRecordName);
00131:             var initialValues = new Dictionary<string, string>(
00132:                 StringComparer.OrdinalIgnoreCase);
00133:             foreach (string field in FieldOrder)
00134:             {
00135:                 string value = existing.Get(field);
00136:                 if (string.IsNullOrWhiteSpace(value) &&
00137:                     string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
00138:                     value = "Metric";
00139:                 if (string.IsNullOrWhiteSpace(value) &&
00140:                     string.Equals(field, "Issue Date", StringComparison.OrdinalIgnoreCase))
00141:                     value = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
00142:                 initialValues[field] = value ?? string.Empty;
00143:             }
00144: 
00145:             var window = new ProjectSetupPopupWindow(
00146:                 FieldOrder,
00147:                 initialValues);
00148:             AcApplication.ShowModalWindow(window);
00149:             if (!window.Accepted)
00150:             {
00151:                 editor.WriteMessage(
00152:                     "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
00153:                 return;
00154:             }
00155: 
00156:             var proposed = new ProjectMetadata();
00157:             foreach (string field in FieldOrder)
00158:                 proposed.Set(field, window.GetValue(field));
00159: 
00160:             if (!PopupTablePresenter.ShowReview(
00161:                 "CE Tools - Project Setup",
00162:                 "Review the project information before it is saved inside this drawing and linked to title blocks and drawing registers.",
00163:                 BuildRows(proposed),
00164:                 "Save"))
00165:             {
00166:                 editor.WriteMessage(
00167:                     "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
00168:                 return;
00169:             }
00170: 
00171:             try
00172:             {
00173:                 WriteProjectMetadata(document.Database, proposed, clearBackup: true);
00174:                 RefreshInformationTables(document);
00175:                 editor.WriteMessage(
00176:                     "\nCE_PROJECTSETUP complete. Project metadata saved inside this DWG.");
00177:                 PopupTablePresenter.ShowReportAndOfferTable(
00178:                     document,
00179:                     "CE Tools - Project Information",
00180:                     "Project setup is complete and is now the shared source for drawing titles and registers.",
00181:                     BuildRows(proposed),
00182:                     "CE Tools Project Information");
00183:             }
00184:             catch (System.Exception exception)
00185:             {
00186:                 editor.WriteMessage(
00187:                     "\nCE_PROJECTSETUP cancelled. Existing metadata was not replaced. {0}",
00188:                     exception.Message);
00189:             }
00190:         }
00191: 
00192:         internal static IDictionary<string, string> ReadSharedProjectMetadata(
00193:             Database database)
00194:         {
00195:             ProjectMetadata metadata = ReadProjectMetadata(
00196:                 database,
00197:                 ProjectRecordName);
00198:             var result = new Dictionary<string, string>(
00199:                 StringComparer.OrdinalIgnoreCase);
00200:             foreach (string field in FieldOrder)
00201:                 result[field] = metadata.Get(field);
00202:             return result;
00203:         }
00204: 
00205:         internal static void MergeSharedProjectMetadata(
00206:             Database database,
00207:             IDictionary<string, string> values)
00208:         {
00209:             ProjectMetadata metadata = ReadProjectMetadata(
00210:                 database,
00211:                 ProjectRecordName);
00212:             foreach (string field in FieldOrder)
00213:             {
00214:                 string value;
00215:                 if (values != null && values.TryGetValue(field, out value))
00216:                     metadata.Set(field, value ?? string.Empty);
00217:             }
00218:             metadata.Exists = true;
00219:             WriteProjectMetadata(database, metadata, clearBackup: false);
00220:         }
00221: 
00222:         private static void ReportProjectInfo(Document document)
00223:         {
00224:             ProjectMetadata metadata = ReadProjectMetadata(
00225:                 document.Database,
00226:                 ProjectRecordName);
00227:             if (!metadata.Exists)
00228:             {
00229:                 document.Editor.WriteMessage(
00230:                     "\nCE_PROJECTINFO: no CE Tools project metadata is stored in this drawing.");
00231:                 return;
00232:             }
00233: 
00234:             document.Editor.WriteMessage("\nCE Tools Project Information");
00235:             WriteMetadata(document.Editor, metadata);
00236:             PopupTablePresenter.ShowReportAndOfferTable(
00237:                 document,
00238:                 "CE Tools - Project Information",
00239:                 "The information below is stored inside the current DWG. Choose Place Table to add a drawing table.",
00240:                 BuildRows(metadata),
00241:                 "CE Tools Project Information");
```

### Lines 331-410
```csharp
00331:             }
00332:             catch (System.Exception exception)
00333:             {
00334:                 editor.WriteMessage(
00335:                     "\nCE_PROJECTRESTORE cancelled. The backup was retained. {0}",
00336:                     exception.Message);
00337:             }
00338:         }
00339: 
00340:         private static PromptResult PromptForValue(
00341:             Editor editor,
00342:             string fieldName,
00343:             string defaultValue)
00344:         {
00345:             string prompt = string.IsNullOrWhiteSpace(defaultValue)
00346:                 ? string.Format("\n{0}: ", fieldName)
00347:                 : string.Format("\n{0} <{1}>: ", fieldName, defaultValue);
00348: 
00349:             var options = new PromptStringOptions(prompt)
00350:             {
00351:                 AllowSpaces = true,
00352:                 UseDefaultValue = !string.IsNullOrWhiteSpace(defaultValue),
00353:                 DefaultValue = defaultValue ?? string.Empty
00354:             };
00355: 
00356:             return editor.GetString(options);
00357:         }
00358: 
00359:         private static ProjectMetadata ReadProjectMetadata(
00360:             Database database,
00361:             string recordName)
00362:         {
00363:             var metadata = new ProjectMetadata();
00364: 
00365:             try
00366:             {
00367:                 using (Transaction transaction = database.TransactionManager.StartTransaction())
00368:                 {
00369:                     DBDictionary namedObjects = transaction.GetObject(
00370:                         database.NamedObjectsDictionaryId,
00371:                         OpenMode.ForRead,
00372:                         false) as DBDictionary;
00373:                     if (namedObjects == null || !namedObjects.Contains(RootDictionaryName))
00374:                     {
00375:                         return metadata;
00376:                     }
00377: 
00378:                     DBDictionary root = transaction.GetObject(
00379:                         namedObjects.GetAt(RootDictionaryName),
00380:                         OpenMode.ForRead,
00381:                         false) as DBDictionary;
00382:                     if (root == null || !root.Contains(recordName))
00383:                     {
00384:                         return metadata;
00385:                     }
00386: 
00387:                     Xrecord record = transaction.GetObject(
00388:                         root.GetAt(recordName),
00389:                         OpenMode.ForRead,
00390:                         false) as Xrecord;
00391:                     if (record == null || record.Data == null)
00392:                     {
00393:                         return metadata;
00394:                     }
00395: 
00396:                     ReadPairs(record.Data, metadata.Set);
00397:                     metadata.Exists = true;
00398:                 }
00399:             }
00400:             catch
00401:             {
00402:                 // A malformed or inaccessible metadata record is treated as absent.
00403:             }
00404: 
00405:             return metadata;
00406:         }
00407: 
00408:         private static void WriteProjectMetadata(
00409:             Database database,
00410:             ProjectMetadata metadata,
```

## ProjectSetupPopupWindow.cs
Hits: `CE_PROJECTSETUP`, `ProjectSetupPopupWindow`

### Lines 1-73
```csharp
00001: using System;
00002: using System.Collections.Generic;
00003: using System.Windows;
00004: using System.Windows.Controls;
00005: using System.Windows.Input;
00006: 
00007: namespace CETools.Civil3D
00008: {
00009:     /// <summary>
00010:     /// One-window editor for the project metadata stored by CE_PROJECTSETUP.
00011:     /// It deliberately contains no database writes; the existing command keeps
00012:     /// ownership of review, transaction, backup and table-placement behaviour.
00013:     /// </summary>
00014:     internal sealed class ProjectSetupPopupWindow : Window
00015:     {
00016:         private readonly Dictionary<string, TextBox> _editors =
00017:             new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
00018: 
00019:         public ProjectSetupPopupWindow(
00020:             IEnumerable<string> fields,
00021:             IDictionary<string, string> initialValues)
00022:         {
00023:             if (fields == null)
00024:                 throw new ArgumentNullException("fields");
00025: 
00026:             Title = "CE Tools - Project Setup";
00027:             Width = 620.0;
00028:             MinWidth = 520.0;
00029:             Height = 560.0;
00030:             MinHeight = 420.0;
00031:             WindowStartupLocation = WindowStartupLocation.CenterScreen;
00032:             ResizeMode = ResizeMode.CanResizeWithGrip;
00033:             ShowInTaskbar = false;
00034: 
00035:             var root = new DockPanel
00036:             {
00037:                 Margin = new Thickness(16.0)
00038:             };
00039:             Content = root;
00040: 
00041:             var buttons = new StackPanel
00042:             {
00043:                 Orientation = Orientation.Horizontal,
00044:                 HorizontalAlignment = HorizontalAlignment.Right,
00045:                 Margin = new Thickness(0.0, 14.0, 0.0, 0.0)
00046:             };
00047:             DockPanel.SetDock(buttons, Dock.Bottom);
00048:             root.Children.Add(buttons);
00049: 
00050:             var cancel = new Button
00051:             {
00052:                 Content = "Cancel",
00053:                 MinWidth = 90.0,
00054:                 Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
00055:                 IsCancel = true
00056:             };
00057:             cancel.Click += delegate
00058:             {
00059:                 Accepted = false;
00060:                 DialogResult = false;
00061:             };
00062:             buttons.Children.Add(cancel);
00063: 
00064:             var review = new Button
00065:             {
00066:                 Content = "Review and Save",
00067:                 MinWidth = 130.0,
00068:                 Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
00069:                 IsDefault = true
00070:             };
00071:             review.Click += delegate
00072:             {
00073:                 Accepted = true;
```

## PumpSystemReviewCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 15-95
```csharp
00015: namespace CETools.Civil3D
00016: {
00017:     /// <summary>
00018:     /// Preliminary pump/system-curve screening for sewer rising mains, water and
00019:     /// bulk-water systems. Manufacturer CSV data remains the user's responsibility.
00020:     /// The workflow does not replace transient analysis, motor/electrical checks,
00021:     /// manufacturer selection or professional hydraulic design.
00022:     /// </summary>
00023:     public sealed class PumpSystemReviewCommands
00024:     {
00025:         private const int MaximumCurveFiles = 100;
00026:         private const int MaximumCurveRows = 10000;
00027: 
00028:         [CommandMethod("CE_TOOLS", "CE_PUMPSYSTEMTOOLS", CommandFlags.Modal)]
00029:         public void PumpSystemTools()
00030:         {
00031:             Document document = ActiveDocument();
00032:             if (document == null) return;
00033:             var options = new PromptKeywordOptions(
00034:                 "\nPump/system curve tools [Template/Single/Folder] <Single>: ")
00035:             {
00036:                 AllowNone = true
00037:             };
00038:             options.Keywords.Add("Template");
00039:             options.Keywords.Add("Single");
00040:             options.Keywords.Add("Folder");
00041:             PromptResult result = document.Editor.GetKeywords(options);
00042:             if (result.Status == PromptStatus.Cancel) return;
00043:             string choice = result.Status == PromptStatus.OK ? result.StringResult : "Single";
00044:             string command = Equal(choice, "Template")
00045:                 ? "CE_PUMPCURVETEMPLATE "
00046:                 : Equal(choice, "Folder")
00047:                     ? "CE_PUMPFOLDERREVIEW "
00048:                     : "CE_PUMPSYSTEMREVIEW ";
00049:             document.SendStringToExecute(command, true, false, true);
00050:         }
00051: 
00052:         [CommandMethod("CE_TOOLS", "CE_PUMPCURVETEMPLATE", CommandFlags.Modal)]
00053:         public void CreatePumpCurveTemplate()
00054:         {
00055:             Document document = ActiveDocument();
00056:             if (document == null) return;
00057:             var saveOptions = new PromptSaveFileOptions(
00058:                 "\nChoose the pump-curve CSV template path: ")
00059:             {
00060:                 Filter = "Comma-separated values (*.csv)|*.csv",
00061:                 DialogCaption = "Create CE Tools Pump Curve Template",
00062:                 InitialFileName = "Pump-Manufacturer-Curve.csv"
00063:             };
00064:             PromptFileNameResult result = document.Editor.GetFileNameForSave(saveOptions);
00065:             if (result.Status != PromptStatus.OK) return;
00066:             string path = EnsureExtension(result.StringResult, ".csv");
00067:             if (File.Exists(path))
00068:             {
00069:                 document.Editor.WriteMessage(
00070:                     "\nCE_PUMPCURVETEMPLATE stopped. Existing files are not overwritten.");
00071:                 return;
00072:             }
00073: 
00074:             Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
00075:             File.WriteAllText(
00076:                 path,
00077:                 "FlowLps,HeadM,EfficiencyPercent,PowerKw,NpshRequiredM\r\n" +
00078:                 "0,35,0,0,2.0\r\n" +
00079:                 "10,30,65,5.5,2.5\r\n" +
00080:                 "20,22,78,8.0,3.2\r\n" +
00081:                 "30,10,70,11.0,4.5\r\n",
00082:                 new UTF8Encoding(false));
00083:             document.Editor.WriteMessage(
00084:                 "\nCE_PUMPCURVETEMPLATE complete. Required columns: FlowLps and HeadM. Optional: EfficiencyPercent, PowerKw and NpshRequiredM. File={0}",
00085:                 path);
00086:         }
00087: 
00088:         [CommandMethod("CE_TOOLS", "CE_PUMPSYSTEMREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
00089:         public void ReviewSinglePump()
00090:         {
00091:             Document document = ActiveDocument();
00092:             if (document == null) return;
00093:             Editor editor = document.Editor;
00094: 
00095:             string curvePath;
```

### Lines 479-558
```csharp
00479:         }
00480: 
00481:         private static bool PromptNonNegativeDouble(Editor editor, string label, double defaultValue, out double value)
00482:         {
00483:             var options = new PromptDoubleOptions("\n" + label)
00484:             {
00485:                 AllowNone = true,
00486:                 AllowNegative = false,
00487:                 AllowZero = true,
00488:                 DefaultValue = defaultValue
00489:             };
00490:             PromptDoubleResult result = editor.GetDouble(options);
00491:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00492:             return result.Status != PromptStatus.Cancel && IsFinite(value) && value >= 0.0;
00493:         }
00494: 
00495:         private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
00496:         {
00497:             var options = new PromptKeywordOptions(
00498:                 "\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00499:             {
00500:                 AllowNone = true
00501:             };
00502:             options.Keywords.Add("Yes");
00503:             options.Keywords.Add("No");
00504:             PromptResult result = editor.GetKeywords(options);
00505:             if (result.Status == PromptStatus.Cancel) return false;
00506:             return result.Status == PromptStatus.None
00507:                 ? defaultValue
00508:                 : Equal(result.StringResult, "Yes");
00509:         }
00510: 
00511:         private static bool PromptExcelPath(Editor editor, string initialName, out string path)
00512:         {
00513:             var options = new PromptSaveFileOptions("\nChoose the Excel workbook path: ")
00514:             {
00515:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
00516:                 DialogCaption = "Export CE Tools Pump Review",
00517:                 InitialFileName = initialName
00518:             };
00519:             PromptFileNameResult result = editor.GetFileNameForSave(options);
00520:             path = result.Status == PromptStatus.OK
00521:                 ? EnsureExtension(result.StringResult, ".xlsx")
00522:                 : string.Empty;
00523:             return result.Status == PromptStatus.OK;
00524:         }
00525: 
00526:         private static List<string> ParseCsvLine(string line)
00527:         {
00528:             var values = new List<string>();
00529:             var current = new StringBuilder();
00530:             bool quoted = false;
00531:             for (int index = 0; index < line.Length; index++)
00532:             {
00533:                 char character = line[index];
00534:                 if (character == '"')
00535:                 {
00536:                     if (quoted && index + 1 < line.Length && line[index + 1] == '"')
00537:                     {
00538:                         current.Append('"');
00539:                         index++;
00540:                     }
00541:                     else quoted = !quoted;
00542:                 }
00543:                 else if (character == ',' && !quoted)
00544:                 {
00545:                     values.Add(current.ToString().Trim());
00546:                     current.Clear();
00547:                 }
00548:                 else current.Append(character);
00549:             }
00550:             values.Add(current.ToString().Trim());
00551:             return values;
00552:         }
00553: 
00554:         private static string NormalizeHeading(string value)
00555:         {
00556:             return new string((value ?? string.Empty)
00557:                 .Where(char.IsLetterOrDigit)
00558:                 .Select(char.ToUpperInvariant)
```

## RefreshAllCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 110-190
```csharp
00110:                 projectInformationTables,
00111:                 restoredLinks,
00112:                 failures.Count);
00113: 
00114:             if (failures.Count > 0)
00115:             {
00116:                 document.Editor.WriteMessage(
00117:                     "\nSkipped modules: {0}. Other linked outputs were still processed.",
00118:                     string.Join("; ", failures));
00119:             }
00120:         }
00121: 
00122:         [CommandMethod("CE_TOOLS", "CE_AUTOREFRESH", CommandFlags.Modal)]
00123:         public void ConfigureAutomaticRefresh()
00124:         {
00125:             Document document = ActiveDocument();
00126:             if (document == null) return;
00127:             bool current = LinkedTableAutoRefreshManager.IsEnabled(document.Database);
00128:             var options = new PromptKeywordOptions(
00129:                 "\nAutomatic linked coordinate, setting-out and BOQ table refresh [On/Off] <" +
00130:                 (current ? "On" : "Off") + ">: ")
00131:             {
00132:                 AllowNone = true
00133:             };
00134:             options.Keywords.Add("On");
00135:             options.Keywords.Add("Off");
00136:             PromptResult result = document.Editor.GetKeywords(options);
00137:             if (result.Status == PromptStatus.Cancel) return;
00138:             bool enabled = result.Status == PromptStatus.None
00139:                 ? current
00140:                 : string.Equals(result.StringResult, "On", StringComparison.OrdinalIgnoreCase);
00141:             LinkedTableAutoRefreshManager.SetEnabled(document.Database, enabled);
00142:             if (enabled) LinkedTableAutoRefreshManager.Queue(document);
00143:             document.Editor.WriteMessage(
00144:                 "\nAutomatic linked coordinate, setting-out and BOQ table refresh is {0}. " +
00145:                 "Parking, dynamic-section and cost-estimate managers retain their specialized settings.",
00146:                 enabled ? "ON" : "OFF");
00147:         }
00148: 
00149:         [CommandMethod(
00150:             "CE_TOOLS",
00151:             "CE_REFRESHSTATUS",
00152:             CommandFlags.Modal | CommandFlags.Redraw)]
00153:         public void RefreshStatus()
00154:         {
00155:             Document document = ActiveDocument();
00156:             if (document == null) return;
00157:             Database database = document.Database;
00158: 
00159:             var rows = new List<KeyValuePair<string, string>>
00160:             {
00161:                 Pair("Dynamic coordinate links", SafeCount(delegate { return DynamicCoordinateLinkStore.CountLinks(database); })),
00162:                 Pair("Linked coordinate tables", SafeCount(delegate { return SurveyCoordinateWorkflowCommands.CountLinkedTables(database); })),
00163:                 Pair("Linked setting-out schedules", SafeCount(delegate { return SettingOutScheduleCommands.CountLinkedTables(database); })),
00164:                 Pair("Linked parking labels", SafeCount(delegate { return ParkingNumberLinkCommands.CountLinkedLabels(database); })),
00165:                 Pair("Linked surface-comparison entities", SafeCount(delegate { return SurfaceComparisonLinkStore.CountLinkedEntities(database); })),
00166:                 Pair("Linked BOQ tables", SafeCount(delegate { return BillOfQuantitiesCommands.CountLinkedTables(database); })),
00167:                 Pair("Linked dynamic cross sections", SafeCount(delegate { return DynamicSectionUpdateManager.CountLinkedSections(document); })),
00168:                 Pair("Automatic linked-table refresh", LinkedTableAutoRefreshManager.IsEnabled(database) ? "On" : "Off"),
00169:                 Pair("Linked-table refresh manager", LinkedTableAutoRefreshManager.IsInitialized ? "Active" : "Inactive"),
00170:                 Pair("Linked-table refresh pending", LinkedTableAutoRefreshManager.HasPendingRefresh(document) ? "Yes" : "No"),
00171:                 Pair("Dynamic section manager", DynamicSectionUpdateManager.IsInitialized ? "Active" : "Inactive"),
00172:                 Pair("Dynamic section refresh pending", DynamicSectionUpdateManager.HasPendingRefresh(document) ? "Yes" : "No"),
00173:                 Pair("Automatic cost-estimate refresh", WaterSewerCostEstimateCommands.IsAutomatic(database) ? "On" : "Off"),
00174:                 Pair("Explicit refresh command", "CE_REFRESHALL")
00175:             };
00176: 
00177:             PopupTablePresenter.ShowReportAndOfferTable(
00178:                 document,
00179:                 "CE Tools - Linked Output Refresh Status",
00180:                 "Counts are read from the active drawing. Issue books and project summaries use their dedicated commands.",
00181:                 rows,
00182:                 "CE TOOLS LINKED OUTPUT REFRESH STATUS");
00183:         }
00184: 
00185:         private static int RefreshCrossSections(Document document)
00186:         {
00187:             int refreshed = 0;
00188:             foreach (ObjectId sourceId in DynamicCrossSectionCommands.FindLinkedSectionSources(document.Database))
00189:             {
00190:                 if (DynamicCrossSectionCommands.RefreshLinkedSection(
```

## ReturnPeriodHydrographCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 256-417
```csharp
00256:                 if (time > current.TimeMinutes) continue;
00257:                 HydrographPoint previous = points[index - 1];
00258:                 double duration = current.TimeMinutes - previous.TimeMinutes;
00259:                 if (duration <= Tolerance)
00260:                     return current.FlowCubicMetresPerSecond;
00261:                 double fraction = (time - previous.TimeMinutes) / duration;
00262:                 return previous.FlowCubicMetresPerSecond +
00263:                     (current.FlowCubicMetresPerSecond -
00264:                         previous.FlowCubicMetresPerSecond) * fraction;
00265:             }
00266:             return points[points.Count - 1].FlowCubicMetresPerSecond;
00267:         }
00268: 
00269:         private static bool PromptDetailPeriod(
00270:             Editor editor,
00271:             out int period)
00272:         {
00273:             period = 100;
00274:             var options = new PromptKeywordOptions(
00275:                 "\nDetailed time-series return period [P2/P5/P10/P20/P25/P50/P100/None] <P100>: ")
00276:             {
00277:                 AllowNone = true
00278:             };
00279:             options.Keywords.Add("P2");
00280:             options.Keywords.Add("P5");
00281:             options.Keywords.Add("P10");
00282:             options.Keywords.Add("P20");
00283:             options.Keywords.Add("P25");
00284:             options.Keywords.Add("P50");
00285:             options.Keywords.Add("P100");
00286:             options.Keywords.Add("None");
00287:             PromptResult result = editor.GetKeywords(options);
00288:             if (result.Status == PromptStatus.Cancel) return false;
00289:             if (result.Status == PromptStatus.None) return true;
00290:             if (string.Equals(
00291:                     result.StringResult,
00292:                     "None",
00293:                     StringComparison.OrdinalIgnoreCase))
00294:             {
00295:                 period = 0;
00296:                 return true;
00297:             }
00298:             return int.TryParse(
00299:                 result.StringResult.Substring(1),
00300:                 NumberStyles.Integer,
00301:                 CultureInfo.InvariantCulture,
00302:                 out period);
00303:         }
00304: 
00305:         private static bool PromptPositiveDouble(
00306:             Editor editor,
00307:             string label,
00308:             double defaultValue,
00309:             out double value)
00310:         {
00311:             var options = new PromptDoubleOptions(
00312:                 "\n" + label + " <" +
00313:                 defaultValue.ToString("0.###", CultureInfo.InvariantCulture) +
00314:                 ">: ")
00315:             {
00316:                 AllowNone = true,
00317:                 AllowNegative = false,
00318:                 AllowZero = false,
00319:                 DefaultValue = defaultValue,
00320:                 UseDefaultValue = true
00321:             };
00322:             PromptDoubleResult result = editor.GetDouble(options);
00323:             if (result.Status == PromptStatus.Cancel)
00324:             {
00325:                 value = defaultValue;
00326:                 return false;
00327:             }
00328:             value = result.Status == PromptStatus.OK
00329:                 ? result.Value
00330:                 : defaultValue;
00331:             return result.Status == PromptStatus.OK ||
00332:                    result.Status == PromptStatus.None;
00333:         }
00334: 
00335:         private static bool PromptRatio(
00336:             Editor editor,
00337:             string label,
00338:             double defaultValue,
00339:             out double value)
00340:         {
00341:             if (!PromptPositiveDouble(editor, label, defaultValue, out value))
00342:                 return false;
00343:             if (value <= 1.0) return true;
00344:             editor.WriteMessage(
00345:                 "\n{0} must be greater than zero and no more than 1.0.",
00346:                 label);
00347:             return false;
00348:         }
00349: 
00350:         private static bool PromptYesNo(
00351:             Editor editor,
00352:             string question,
00353:             bool defaultYes)
00354:         {
00355:             var options = new PromptKeywordOptions(
00356:                 "\n" + question + " [Yes/No] <" +
00357:                 (defaultYes ? "Yes" : "No") + ">: ")
00358:             {
00359:                 AllowNone = true
00360:             };
00361:             options.Keywords.Add("Yes");
00362:             options.Keywords.Add("No");
00363:             PromptResult result = editor.GetKeywords(options);
00364:             if (result.Status == PromptStatus.Cancel) return false;
00365:             return result.Status == PromptStatus.None
00366:                 ? defaultYes
00367:                 : string.Equals(
00368:                     result.StringResult,
00369:                     "Yes",
00370:                     StringComparison.OrdinalIgnoreCase);
00371:         }
00372: 
00373:         private static bool PromptExcelPath(
00374:             Editor editor,
00375:             string defaultName,
00376:             out string path)
00377:         {
00378:             path = string.Empty;
00379:             var options = new PromptSaveFileOptions(
00380:                 "\nChoose the return-period hydrograph Excel workbook path: ")
00381:             {
00382:                 DialogCaption = "Export CE Tools Return-Period Hydrographs",
00383:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
00384:                 InitialFileName = defaultName
00385:             };
00386:             PromptFileNameResult result = editor.GetFileNameForSave(options);
00387:             if (result.Status != PromptStatus.OK) return false;
00388:             path = result.StringResult;
00389:             if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
00390:                 path += ".xlsx";
00391:             return true;
00392:         }
00393: 
00394:         private static string Format(double value)
00395:         {
00396:             return value.ToString("0.######", CultureInfo.InvariantCulture);
00397:         }
00398: 
00399:         private static Document ActiveDocument()
00400:         {
00401:             return AcApplication.DocumentManager.MdiActiveDocument;
00402:         }
00403:     }
00404: 
00405:     internal sealed class ReturnPeriodHydrographScenario
00406:     {
00407:         public ReturnPeriodHydrographScenario(
00408:             int returnPeriod,
00409:             double intensity,
00410:             HydrographSeries pre,
00411:             HydrographSeries post,
00412:             double preVolumeCubicMetres,
00413:             double postVolumeCubicMetres)
00414:         {
00415:             ReturnPeriod = returnPeriod;
00416:             Intensity = intensity;
00417:             Pre = pre;
```

## RoadCrossSectionScheduleCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 17-98
```csharp
00017: 
00018: namespace CETools.Civil3D
00019: {
00020:     /// <summary>
00021:     /// Linked road cross-section setting-out schedules at a configurable interval.
00022:     /// Each station produces left-edge, centreline and right-edge rows with X, Y,
00023:     /// ground elevation, design elevation and elevation difference.
00024:     /// </summary>
00025:     public sealed class RoadCrossSectionScheduleCommands
00026:     {
00027:         private const string LinkRecordName = "CE_ROAD_SECTION_SCHEDULE";
00028:         private const string SchemaVersion = "1";
00029: 
00030:         [CommandMethod("CE_TOOLS", "CE_ROADSECTIONDATATOOLS", CommandFlags.Modal)]
00031:         public void RoadSectionDataTools()
00032:         {
00033:             Document document = ActiveDocument();
00034:             if (document == null) return;
00035:             var options = new PromptKeywordOptions(
00036:                 "\nRoad section data [Create/Refresh/Export/Info] <Create>: ")
00037:             {
00038:                 AllowNone = true
00039:             };
00040:             options.Keywords.Add("Create");
00041:             options.Keywords.Add("Refresh");
00042:             options.Keywords.Add("Export");
00043:             options.Keywords.Add("Info");
00044:             PromptResult result = document.Editor.GetKeywords(options);
00045:             if (result.Status == PromptStatus.Cancel) return;
00046:             string choice = result.Status == PromptStatus.OK
00047:                 ? result.StringResult
00048:                 : "Create";
00049:             string command;
00050:             if (string.Equals(choice, "Refresh", StringComparison.OrdinalIgnoreCase))
00051:                 command = "CE_ROADSECTIONDATAREFRESH ";
00052:             else if (string.Equals(choice, "Export", StringComparison.OrdinalIgnoreCase))
00053:                 command = "CE_ROADSECTIONDATAEXPORT ";
00054:             else if (string.Equals(choice, "Info", StringComparison.OrdinalIgnoreCase))
00055:                 command = "CE_ROADSECTIONDATAINFO ";
00056:             else
00057:                 command = "CE_ROADSECTIONDATA ";
00058:             document.SendStringToExecute(command, true, false, true);
00059:         }
00060: 
00061:         [CommandMethod("CE_TOOLS", "CE_ROADSECTIONDATA", CommandFlags.Modal | CommandFlags.Redraw)]
00062:         public void CreateRoadSectionData()
00063:         {
00064:             Document document = ActiveDocument();
00065:             if (document == null) return;
00066:             Editor editor = document.Editor;
00067: 
00068:             PromptEntityResult alignmentResult = PromptAlignment(
00069:                 editor,
00070:                 "\nSelect the road alignment for cross-section setting-out data: ");
00071:             if (alignmentResult.Status != PromptStatus.OK) return;
00072: 
00073:             List<RoadSectionSurfaceChoice> surfaces = ReadSurfaceChoices(document);
00074:             var window = new RoadSectionConfigurationWindow(surfaces);
00075:             AcApplication.ShowModalWindow(window);
00076:             if (!window.Accepted)
00077:             {
00078:                 editor.WriteMessage("\nCE_ROADSECTIONDATA cancelled.");
00079:                 return;
00080:             }
00081: 
00082:             PromptPointResult insertion = editor.GetPoint(
00083:                 "\nPick insertion point for the linked road cross-section data table: ");
00084:             if (insertion.Status != PromptStatus.OK) return;
00085:             AnnotationOptions annotation;
00086:             if (!AnnotationSettingsStore.Prepare(document, false, out annotation))
00087:                 return;
00088: 
00089:             var link = new RoadSectionLink(
00090:                 alignmentResult.ObjectId.Handle.ToString(),
00091:                 window.GroundChoice,
00092:                 window.DesignChoice,
00093:                 window.Interval,
00094:                 window.LeftOffset,
00095:                 window.RightOffset);
00096:             int failed;
00097:             List<RoadSectionRow> rows;
00098:             using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
```

### Lines 679-758
```csharp
00679:         private static bool TryResolveHandle(Database database, string text, out ObjectId id)
00680:         {
00681:             id = ObjectId.Null;
00682:             long value;
00683:             if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
00684:             try
00685:             {
00686:                 id = database.GetObjectId(false, new Handle(value), 0);
00687:                 return !id.IsNull && !id.IsErased;
00688:             }
00689:             catch
00690:             {
00691:                 return false;
00692:             }
00693:         }
00694: 
00695:         private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
00696:         {
00697:             var options = new PromptKeywordOptions(
00698:                 "\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00699:             {
00700:                 AllowNone = true
00701:             };
00702:             options.Keywords.Add("Yes");
00703:             options.Keywords.Add("No");
00704:             PromptResult result = editor.GetKeywords(options);
00705:             if (result.Status == PromptStatus.Cancel) return false;
00706:             return result.Status == PromptStatus.None
00707:                 ? defaultValue
00708:                 : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00709:         }
00710: 
00711:         private static double NormalizeHeight(double value)
00712:         {
00713:             if (Math.Abs(value - 1.8) < 0.05) return 1.8;
00714:             if (Math.Abs(value - 5.0) < 0.05) return 5.0;
00715:             return 2.0;
00716:         }
00717: 
00718:         private static string FormatStation(double station)
00719:         {
00720:             int kilometres = (int)Math.Floor(station / 1000.0);
00721:             double remainder = station - (kilometres * 1000.0);
00722:             return kilometres.ToString(CultureInfo.InvariantCulture) + "+" +
00723:                 remainder.ToString("000.000", CultureInfo.CurrentCulture);
00724:         }
00725: 
00726:         private static string FormatNullable(double? value)
00727:         {
00728:             return value.HasValue ? value.Value.ToString("N3", CultureInfo.CurrentCulture) : string.Empty;
00729:         }
00730: 
00731:         private static KeyValuePair<string, string> Pair(string key, string value)
00732:         {
00733:             return new KeyValuePair<string, string>(key, value);
00734:         }
00735: 
00736:         private static Document ActiveDocument()
00737:         {
00738:             return AcApplication.DocumentManager.MdiActiveDocument;
00739:         }
00740:     }
00741: 
00742:     internal sealed class RoadSectionLink
00743:     {
00744:         public RoadSectionLink(
00745:             string alignmentHandle,
00746:             RoadSectionSurfaceChoice ground,
00747:             RoadSectionSurfaceChoice design,
00748:             double interval,
00749:             double leftOffset,
00750:             double rightOffset)
00751:         {
00752:             AlignmentHandle = alignmentHandle;
00753:             Ground = ground ?? RoadSectionSurfaceChoice.Blank();
00754:             Design = design ?? RoadSectionSurfaceChoice.Blank();
00755:             Interval = Math.Max(interval, 0.001);
00756:             LeftOffset = Math.Abs(leftOffset);
00757:             RightOffset = Math.Abs(rightOffset);
00758:         }
```

## RoadDriveReviewCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 23-102
```csharp
00023:     /// <summary>
00024:     /// Preliminary road-drive and geometry-error review from a Civil 3D alignment
00025:     /// and profile. Source Civil objects remain read-only. The workflow does not
00026:     /// replace formal geometric design, sight-distance, superelevation, collision,
00027:     /// corridor or vehicle-dynamics analysis.
00028:     /// </summary>
00029:     public sealed class RoadDriveReviewCommands
00030:     {
00031:         private const string RegAppName = "CE_ROAD_DRIVE_REVIEW";
00032:         private const string ReviewLayer = "CE-ROAD-DRIVE-REVIEW";
00033:         private const int MaximumSamples = 100000;
00034:         private const int MaximumIssueLabels = 500;
00035: 
00036:         [CommandMethod("CE_TOOLS", "CE_ROADDRIVETOOLS", CommandFlags.Modal)]
00037:         public void RoadDriveTools()
00038:         {
00039:             Document document = ActiveDocument();
00040:             if (document == null) return;
00041:             var options = new PromptKeywordOptions(
00042:                 "\nRoad-drive tools [Review/Export/Info/Clear] <Review>: ")
00043:             {
00044:                 AllowNone = true
00045:             };
00046:             foreach (string keyword in new[] { "Review", "Export", "Info", "Clear" })
00047:                 options.Keywords.Add(keyword);
00048:             PromptResult result = document.Editor.GetKeywords(options);
00049:             if (result.Status == PromptStatus.Cancel) return;
00050:             string choice = result.Status == PromptStatus.OK ? result.StringResult : "Review";
00051:             string command = Equal(choice, "Export")
00052:                 ? "CE_ROADDRIVEEXPORT "
00053:                 : Equal(choice, "Info")
00054:                     ? "CE_ROADDRIVEINFO "
00055:                     : Equal(choice, "Clear")
00056:                         ? "CE_ROADDRIVECLEAR "
00057:                         : "CE_ROADDRIVEREVIEW ";
00058:             document.SendStringToExecute(command, true, false, true);
00059:         }
00060: 
00061:         [CommandMethod("CE_TOOLS", "CE_ROADDRIVEREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
00062:         public void ReviewRoadDrive()
00063:         {
00064:             Document document = ActiveDocument();
00065:             if (document == null) return;
00066:             Editor editor = document.Editor;
00067: 
00068:             RoadDriveSource source;
00069:             if (!PromptSource(document, out source)) return;
00070:             RoadDriveInput input;
00071:             if (!PromptReviewInput(editor, out input)) return;
00072: 
00073:             try
00074:             {
00075:                 List<RoadDriveSample> samples = ReadSamples(
00076:                     document.Database,
00077:                     source,
00078:                     input.SampleIntervalMetres);
00079:                 RoadDriveAnalysis analysis = RoadDriveReviewer.Analyse(
00080:                     samples,
00081:                     input.Criteria);
00082:                 List<IList<string>> rows = BuildReviewRows(source, input, analysis);
00083:                 string subtitle = string.Format(
00084:                     CultureInfo.CurrentCulture,
00085:                     "Alignment={0}; profile={1}; samples={2}; issues={3}; speed={4:N1} km/h. Results are preliminary design screening only.",
00086:                     source.AlignmentName,
00087:                     source.ProfileName,
00088:                     analysis.Samples.Count,
00089:                     analysis.Issues.Count,
00090:                     input.Criteria.DesignSpeedKilometresPerHour);
00091: 
00092:                 GridReportPresenter.ShowReportAndOfferTable(
00093:                     document,
00094:                     "CE Tools - Road Drive and Design Review",
00095:                     subtitle,
00096:                     rows,
00097:                     "CE TOOLS ROAD DRIVE REVIEW");
00098: 
00099:                 if (!PromptYesNo(editor, "Create the 3D drive path and issue markers", true))
00100:                     return;
00101: 
00102:                 int created = CreateReviewGraphics(
```

### Lines 687-766
```csharp
00687:         }
00688: 
00689:         private static bool PromptNonNegativeDouble(Editor editor, string label, double defaultValue, out double value)
00690:         {
00691:             var options = new PromptDoubleOptions("\n" + label)
00692:             {
00693:                 AllowNone = true,
00694:                 AllowNegative = false,
00695:                 AllowZero = true,
00696:                 DefaultValue = defaultValue
00697:             };
00698:             PromptDoubleResult result = editor.GetDouble(options);
00699:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00700:             return result.Status != PromptStatus.Cancel && IsFinite(value) && value >= 0.0;
00701:         }
00702: 
00703:         private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
00704:         {
00705:             var options = new PromptKeywordOptions(
00706:                 "\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00707:             {
00708:                 AllowNone = true
00709:             };
00710:             options.Keywords.Add("Yes");
00711:             options.Keywords.Add("No");
00712:             PromptResult result = editor.GetKeywords(options);
00713:             if (result.Status == PromptStatus.Cancel) return false;
00714:             return result.Status == PromptStatus.None
00715:                 ? defaultValue
00716:                 : Equal(result.StringResult, "Yes");
00717:         }
00718: 
00719:         private static bool PromptExcelPath(Editor editor, string initialName, out string path)
00720:         {
00721:             var options = new PromptSaveFileOptions("\nChoose the Excel workbook path: ")
00722:             {
00723:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
00724:                 DialogCaption = "Export CE Tools Road Drive Review",
00725:                 InitialFileName = initialName
00726:             };
00727:             PromptFileNameResult result = editor.GetFileNameForSave(options);
00728:             path = result.Status == PromptStatus.OK
00729:                 ? EnsureExtension(result.StringResult, ".xlsx")
00730:                 : string.Empty;
00731:             return result.Status == PromptStatus.OK;
00732:         }
00733: 
00734:         private static string Csv(string value)
00735:         {
00736:             return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
00737:         }
00738: 
00739:         private static string Format(double value)
00740:         {
00741:             return value.ToString("0.###", CultureInfo.CurrentCulture);
00742:         }
00743: 
00744:         private static string FormatOptional(double? value)
00745:         {
00746:             return value.HasValue ? Format(value.Value) : string.Empty;
00747:         }
00748: 
00749:         private static string EnsureExtension(string path, string extension)
00750:         {
00751:             return path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? path : path + extension;
00752:         }
00753: 
00754:         private static bool IsFinite(double value)
00755:         {
00756:             return !double.IsNaN(value) && !double.IsInfinity(value);
00757:         }
00758: 
00759:         private static bool Equal(string left, string right)
00760:         {
00761:             return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
00762:         }
00763: 
00764:         private static Document ActiveDocument()
00765:         {
00766:             return AcApplication.DocumentManager.MdiActiveDocument;
```

## RoadProductionCommentCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 64-137
```csharp
00064:         }
00065: 
00066:         [CommandMethod("CE_TOOLS", "CE_ROADALIGN", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00067:         public void CreateRoadAlignments()
00068:         {
00069:             Document document = ActiveDocument();
00070:             if (document == null) return;
00071:             CivilDocument civilDocument = CivilApplication.ActiveDocument;
00072:             if (civilDocument == null)
00073:             {
00074:                 document.Editor.WriteMessage("\nCE_ROADALIGN cancelled. No active Civil 3D document is available.");
00075:                 return;
00076:             }
00077: 
00078:             PromptSelectionResult selection = GetSelection(
00079:                 document.Editor,
00080:                 "\nSelect open lightweight polylines for sequential road alignments: ");
00081:             if (selection.Status != PromptStatus.OK) return;
00082:             PromptResult prefixResult = document.Editor.GetString(
00083:                 new PromptStringOptions("\nRoad alignment prefix <RD>: ")
00084:                 {
00085:                     AllowSpaces = false,
00086:                     DefaultValue = "RD",
00087:                     UseDefaultValue = true
00088:                 });
00089:             if (prefixResult.Status != PromptStatus.OK) return;
00090:             PromptIntegerResult startResult = document.Editor.GetInteger(
00091:                 new PromptIntegerOptions("\nStarting road number <1>: ")
00092:                 {
00093:                     AllowNegative = false,
00094:                     AllowZero = false,
00095:                     DefaultValue = 1,
00096:                     LowerLimit = 1,
00097:                     UseDefaultValue = true
00098:                 });
00099:             if (startResult.Status != PromptStatus.OK) return;
00100: 
00101:             List<RoadPolylineSource> sources = ReadPolylineSources(document.Database, selection);
00102:             if (sources.Count == 0)
00103:             {
00104:                 document.Editor.WriteMessage("\nCE_ROADALIGN cancelled. No open lightweight polylines were selected.");
00105:                 return;
00106:             }
00107:             ProjectRoadStyles styles = ResolveRoadStyles(document, civilDocument);
00108:             var previewRows = new List<IList<string>>();
00109:             for (int index = 0; index < sources.Count; index++)
00110:             {
00111:                 previewRows.Add(new List<string>
00112:                 {
00113:                     BuildRoadName(prefixResult.StringResult, startResult.Value + index),
00114:                     sources[index].Layer,
00115:                     sources[index].Length.ToString("N3", CultureInfo.CurrentCulture),
00116:                     sources[index].Handle
00117:                 });
00118:             }
00119:             GridReportPresenter.ShowReportAndOfferTable(
00120:                 document,
00121:                 "CE Tools - Road Alignment Preview",
00122:                 string.Format(
00123:                     CultureInfo.CurrentCulture,
00124:                     "Roads={0}; alignment style={1}; label set={2}; source polylines are retained.",
00125:                     sources.Count,
00126:                     styles.AlignmentStyleName,
00127:                     styles.AlignmentLabelSetName),
00128:                 new List<string> { "Road", "Source Layer", "Length", "Source Handle" },
00129:                 previewRows,
00130:                 "CE TOOLS ROAD ALIGNMENT PREVIEW");
00131:             if (!Confirm(document.Editor, "Create these road alignments")) return;
00132: 
00133:             int created = 0;
00134:             var createdRows = new List<IList<string>>();
00135:             try
00136:             {
00137:                 using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
```

### Lines 1167-1242
```csharp
01167:                 {
01168:                     BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
01169:                     if (block == null || block.IsFromExternalReference) continue;
01170:                     foreach (ObjectId id in block)
01171:                     {
01172:                         DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
01173:                         string type;
01174:                         string road;
01175:                         string source;
01176:                         if (TryReadTag(value, out type, out road, out source) && type == expectedType) count++;
01177:                     }
01178:                 }
01179:             }
01180:             return count;
01181:         }
01182: 
01183:         private static bool Confirm(Editor editor, string message)
01184:         {
01185:             var options = new PromptKeywordOptions("\n" + message + "? [Yes/No] <No>: ") { AllowNone = true };
01186:             options.Keywords.Add("Yes");
01187:             options.Keywords.Add("No");
01188:             PromptResult result = editor.GetKeywords(options);
01189:             return result.Status == PromptStatus.OK && string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
01190:         }
01191: 
01192:         private static PromptSelectionResult GetSelection(Editor editor, string message)
01193:         {
01194:             PromptSelectionResult implied = editor.SelectImplied();
01195:             if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
01196:             {
01197:                 editor.SetImpliedSelection(new ObjectId[0]);
01198:                 return implied;
01199:             }
01200:             return editor.GetSelection(new PromptSelectionOptions
01201:             {
01202:                 MessageForAdding = message,
01203:                 AllowDuplicates = false,
01204:                 RejectObjectsFromNonCurrentSpace = true
01205:             });
01206:         }
01207: 
01208:         private static DisciplineWorkflowAction RoadAction(
01209:             string title,
01210:             string command,
01211:             string description,
01212:             string group)
01213:         {
01214:             return new DisciplineWorkflowAction(title, command, description, group);
01215:         }
01216: 
01217:         private static Document ActiveDocument()
01218:         {
01219:             return AcApplication.DocumentManager.MdiActiveDocument;
01220:         }
01221: 
01222:         private sealed class RoadPolylineSource
01223:         {
01224:             public ObjectId ObjectId { get; set; }
01225:             public string Handle { get; set; }
01226:             public string Layer { get; set; }
01227:             public double Length { get; set; }
01228:         }
01229: 
01230:         private sealed class RoadAlignmentRecord
01231:         {
01232:             public ObjectId AlignmentId { get; set; }
01233:             public string Name { get; set; }
01234:             public string SourceHandle { get; set; }
01235:         }
01236: 
01237:         private sealed class RoadCorridorPlan
01238:         {
01239:             public string RoadName { get; set; }
01240:             public ObjectId AlignmentId { get; set; }
01241:             public ObjectId ProfileId { get; set; }
01242:             public string ProfileName { get; set; }
```

## SettingOutScheduleCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 723-803
```csharp
00723:         private static PromptSelectionResult GetSelection(Editor editor, string message)
00724:         {
00725:             PromptSelectionResult implied = editor.SelectImplied();
00726:             if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
00727:             {
00728:                 editor.SetImpliedSelection(new ObjectId[0]);
00729:                 return implied;
00730:             }
00731:             return editor.GetSelection(new PromptSelectionOptions
00732:             {
00733:                 MessageForAdding = message,
00734:                 AllowDuplicates = false,
00735:                 RejectObjectsFromNonCurrentSpace = true
00736:             });
00737:         }
00738: 
00739:         private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
00740:         {
00741:             var options = new PromptKeywordOptions(
00742:                 "\n" + message + " [Yes/No] <" +
00743:                 (defaultValue ? "Yes" : "No") + ">: ")
00744:             {
00745:                 AllowNone = true
00746:             };
00747:             options.Keywords.Add("Yes");
00748:             options.Keywords.Add("No");
00749:             PromptResult result = editor.GetKeywords(options);
00750:             if (result.Status == PromptStatus.Cancel) return false;
00751:             return result.Status == PromptStatus.None
00752:                 ? defaultValue
00753:                 : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00754:         }
00755: 
00756:         private static bool TryResolveHandle(Database database, string handleText, out ObjectId objectId)
00757:         {
00758:             objectId = ObjectId.Null;
00759:             long value;
00760:             if (!long.TryParse(
00761:                     handleText,
00762:                     NumberStyles.HexNumber,
00763:                     CultureInfo.InvariantCulture,
00764:                     out value))
00765:                 return false;
00766:             try
00767:             {
00768:                 objectId = database.GetObjectId(false, new Handle(value), 0);
00769:                 return !objectId.IsNull && !objectId.IsErased;
00770:             }
00771:             catch
00772:             {
00773:                 return false;
00774:             }
00775:         }
00776: 
00777:         private static double NormalizeHeight(double value)
00778:         {
00779:             return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value)
00780:                 ? value
00781:                 : 0.002;
00782:         }
00783: 
00784:         private static string FormatNullable(double? value)
00785:         {
00786:             return value.HasValue
00787:                 ? value.Value.ToString("N3", CultureInfo.CurrentCulture)
00788:                 : string.Empty;
00789:         }
00790: 
00791:         private static string SanitizeFileName(string value)
00792:         {
00793:             if (string.IsNullOrWhiteSpace(value)) return "General";
00794:             foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
00795:                 value = value.Replace(invalid, '-');
00796:             return value.Replace(' ', '-');
00797:         }
00798: 
00799:         private static KeyValuePair<string, string> Pair(string key, string value)
00800:         {
00801:             return new KeyValuePair<string, string>(key, value);
00802:         }
00803: 
```

## SettingsCenterCommands.cs
Hits: `CE_PROJECTSETUP`

### Lines 63-135
```csharp
00063: 
00064:             var window = new SettingsCenterWindow(SettingsCenterItem.All);
00065:             AcApplication.ShowModalWindow(window);
00066:             if (string.IsNullOrWhiteSpace(window.SelectedCommand)) return;
00067: 
00068:             document.SendStringToExecute(
00069:                 window.SelectedCommand.Trim() + " ",
00070:                 true,
00071:                 false,
00072:                 true);
00073:         }
00074:     }
00075: 
00076:     internal sealed class SettingsCenterItem
00077:     {
00078:         private static readonly IList<SettingsCenterItem> Items =
00079:             new List<SettingsCenterItem>
00080:             {
00081:                 Item("General", "Project setup", "CE_PROJECTSETUP", "Project identity, client, issue and drawing metadata."),
00082:                 Item("General", "Import project styles", "CE_PROJECTSTYLEIMPORT", "Import approved Civil 3D styles from supplied or browsed DWG/DWT sources."),
00083:                 Item("General", "Project style centre", "CE_PROJECTSTYLES", "Alignment, profile, corridor, point and network style selections."),
00084:                 Item("General", "Annotation settings", "CE_ANNOTSETTINGS", "Paper text height, markers and annotation output."),
00085:                 Item("General", "Undo settings", "CE_UNDOSETTINGS", "Enable full native AutoCAD undo recording."),
00086:                 Item("General", "Ribbon icons", "CE_RIBBONICONS", "Review and select the installed ribbon icon mode."),
00087:                 Item("General", "Asset library", "CE_ASSETLIBSETTINGS", "Configure engineering asset library locations."),
00088:                 Item("General", "Typical-detail root", "CE_DETAILSETROOT", "Select the approved typical-detail source directory."),
00089:                 Item("General", "Typical-detail review", "CE_DETAILREVIEWSETTINGS", "Configure typical-detail review and provenance rules."),
00090:                 Item("Survey", "Setting-out schedule", "CE_SETTINGOUTTOOLS", "Create, refresh, export and inspect linked setting-out schedules."),
00091:                 Item("Survey", "Coordinate annotation", "CE_COORDINATE", "Coordinate labels, crosses and linked tables."),
00092:                 Item("Roads", "Surface correction", "CE_SURFCSETTINGS", "Surface audit and conservative correction thresholds."),
00093:                 Item("Roads", "Dynamic intersections", "CE_INTSETTINGS", "Marker, tolerance, sampling and corridor-code settings."),
00094:                 Item("Parking", "Parking skew", "CE_PKSKSETTINGS", "Bay width, skew tolerance, layers and text size."),
00095:                 Item("Parking", "Parking alternatives", "CE_PARKOPTIONS", "Generate and manage linked parking layout options."),
00096:                 Item("Stormwater", "Stormwater production", "CE_SWSETTINGS", "Project styles, layers, labels, profiles and band defaults."),
00097:                 Item("Sewer", "Sewer production", "CE_SEWSETTINGS", "Branch, alignment, profile, label and style defaults."),
00098:                 Item("Water", "Water production", "CE_WATERSETTINGS", "Water alignment, profile, style, band and spacing defaults."),
00099:                 Item("Flood", "Flood result frames", "CE_FLOODFRAMESET", "Configure imported flood-result review frames."),
00100:                 Item("Flood", "Reset flood frames", "CE_FLOODFRAMERESET", "Restore the default flood-result frame configuration."),
00101:                 Item("Production", "Automatic refresh", "CE_AUTOREFRESH", "Configure deferred linked-table and output refresh."),
00102:                 Item("Production", "Dynamic detail parameters", "CE_DETAILPARAMSETTINGS", "Configure dimensions, units and annotation for linked details.")
00103:             };
00104: 
00105:         private SettingsCenterItem(
00106:             string discipline,
00107:             string title,
00108:             string command,
00109:             string description)
00110:         {
00111:             Discipline = discipline;
00112:             Title = title;
00113:             Command = command;
00114:             Description = description;
00115:         }
00116: 
00117:         public string Discipline { get; private set; }
00118:         public string Title { get; private set; }
00119:         public string Command { get; private set; }
00120:         public string Description { get; private set; }
00121: 
00122:         public static IList<SettingsCenterItem> All
00123:         {
00124:             get { return Items; }
00125:         }
00126: 
00127:         private static SettingsCenterItem Item(
00128:             string discipline,
00129:             string title,
00130:             string command,
00131:             string description)
00132:         {
00133:             return new SettingsCenterItem(discipline, title, command, description);
00134:         }
00135:     }
```

## SewerExcavationCommentCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 794-872
```csharp
00794:             return editor.GetSelection(new PromptSelectionOptions
00795:             {
00796:                 MessageForAdding = message,
00797:                 AllowDuplicates = false,
00798:                 RejectObjectsFromNonCurrentSpace = true
00799:             });
00800:         }
00801: 
00802:         private static PromptEntityResult PromptForLinkedTable(Editor editor, string message)
00803:         {
00804:             var options = new PromptEntityOptions(message);
00805:             options.SetRejectMessage("\nSelect an AutoCAD table.");
00806:             options.AddAllowedClass(typeof(Table), false);
00807:             return editor.GetEntity(options);
00808:         }
00809: 
00810:         private static bool Confirm(Editor editor, string message)
00811:         {
00812:             var options = new PromptKeywordOptions("\n" + message + "? [Yes/No] <No>: ")
00813:             {
00814:                 AllowNone = true
00815:             };
00816:             options.Keywords.Add("Yes");
00817:             options.Keywords.Add("No");
00818:             PromptResult result = editor.GetKeywords(options);
00819:             return result.Status == PromptStatus.OK &&
00820:                    string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00821:         }
00822: 
00823:         private static Document ActiveDocument()
00824:         {
00825:             return AcApplication.DocumentManager.MdiActiveDocument;
00826:         }
00827: 
00828:         private sealed class PipeExcavationRow
00829:         {
00830:             public string Handle { get; set; }
00831:             public string Name { get; set; }
00832:             public string Layer { get; set; }
00833:             public double Length { get; set; }
00834:             public double Diameter { get; set; }
00835:             public double AverageCover { get; set; }
00836:             public double TrenchWidth { get; set; }
00837:             public double TrenchDepth { get; set; }
00838:             public double Excavation { get; set; }
00839:             public double Bedding { get; set; }
00840:             public double Backfill { get; set; }
00841:         }
00842: 
00843:         private sealed class ExtractionResult
00844:         {
00845:             public ExtractionResult()
00846:             {
00847:                 Rows = new List<PipeExcavationRow>();
00848:                 UsableHandles = new List<string>();
00849:                 Rejections = new List<string>();
00850:             }
00851:             public List<PipeExcavationRow> Rows { get; }
00852:             public List<string> UsableHandles { get; }
00853:             public List<string> Rejections { get; }
00854:         }
00855: 
00856:         private sealed class SewerExcavationLink
00857:         {
00858:             public SewerExcavationLink(
00859:                 string schema,
00860:                 SewerExcavationSettings settings,
00861:                 IEnumerable<string> handles)
00862:             {
00863:                 Schema = string.IsNullOrWhiteSpace(schema) ? LinkSchema : schema;
00864:                 Settings = settings;
00865:                 Handles = handles == null
00866:                     ? new List<string>()
00867:                     : handles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
00868:             }
00869:             public string Schema { get; }
00870:             public SewerExcavationSettings Settings { get; }
00871:             public List<string> Handles { get; }
00872:         }
```

## SewerProductionCommands.cs
Hits: `PromptStringOptions`, `GetString(`

### Lines 1461-1538
```csharp
01461:             }
01462:             catch
01463:             {
01464:                 return false;
01465:             }
01466:         }
01467: 
01468:         private static int BranchNumber(string branchName)
01469:         {
01470:             string digits = new string((branchName ?? string.Empty).Where(char.IsDigit).ToArray());
01471:             int value;
01472:             return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value)
01473:                 ? value
01474:                 : int.MaxValue;
01475:         }
01476: 
01477:         private static bool PromptText(Editor editor, string label, string current, out string value)
01478:         {
01479:             PromptStringOptions options = new PromptStringOptions(
01480:                 "\n" + label + " <" + Display(current) + ">: ")
01481:             {
01482:                 AllowSpaces = true
01483:             };
01484:             PromptResult result = editor.GetString(options);
01485:             if (result.Status == PromptStatus.Cancel)
01486:             {
01487:                 value = current;
01488:                 return false;
01489:             }
01490:             value = result.Status == PromptStatus.OK ? result.StringResult.Trim() : current;
01491:             return true;
01492:         }
01493: 
01494:         private static string Display(string value)
01495:         {
01496:             return string.IsNullOrWhiteSpace(value) ? "first available" : value;
01497:         }
01498: 
01499:         private static bool Confirm(Editor editor, string message)
01500:         {
01501:             return DisciplineWorkflowDialogs.Confirm("CE Tools — Sewer", message + "?");
01502:         }
01503: 
01504:         private sealed class SewerAlignmentRecord
01505:         {
01506:             public SewerAlignmentRecord(ObjectId alignmentId, string branchKey, string networkHandle, string branchName)
01507:             {
01508:                 AlignmentId = alignmentId;
01509:                 BranchKey = branchKey;
01510:                 NetworkHandle = networkHandle;
01511:                 BranchName = branchName;
01512:             }
01513:             public ObjectId AlignmentId { get; }
01514:             public string BranchKey { get; }
01515:             public string NetworkHandle { get; }
01516:             public string BranchName { get; }
01517:         }
01518: 
01519:         private sealed class SewerSequencePlan
01520:         {
01521:             public SewerSequencePlan(SewerGraph graph, SewerPath main, IReadOnlyList<SewerPath> branches)
01522:             {
01523:                 Graph = graph;
01524:                 Main = main;
01525:                 Branches = branches;
01526:             }
01527:             public SewerGraph Graph { get; }
01528:             public SewerPath Main { get; }
01529:             public IReadOnlyList<SewerPath> Branches { get; }
01530:         }
01531: 
01532:         private sealed class SewerCandidate
01533:         {
01534:             public SewerCandidate(ObjectId root, int rootOrder, SewerPath path)
01535:             {
01536:                 Root = root;
01537:                 RootOrder = rootOrder;
01538:                 Path = path;
```

## SewerSequenceCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 29-109
```csharp
00029:         private const double ElevationTolerance = 1e-9;
00030: 
00031:         private static readonly Regex BranchPattern = new Regex(
00032:             @"^Branch\s*-\s*(\d+)$",
00033:             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
00034: 
00035:         [CommandMethod(
00036:             "CE_TOOLS",
00037:             "CE_SEWSEQ",
00038:             CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
00039:         public void Execute()
00040:         {
00041:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00042:             if (document == null)
00043:             {
00044:                 return;
00045:             }
00046: 
00047:             var options = new PromptKeywordOptions(
00048:                 "\nSewer sequencing mode [EntireNetwork/SelectedPath] <EntireNetwork>: ")
00049:             {
00050:                 AllowNone = true
00051:             };
00052:             options.Keywords.Add("EntireNetwork");
00053:             options.Keywords.Add("SelectedPath");
00054: 
00055:             PromptResult result = document.Editor.GetKeywords(options);
00056:             if (result.Status == PromptStatus.Cancel)
00057:             {
00058:                 return;
00059:             }
00060: 
00061:             string mode = result.Status == PromptStatus.None
00062:                 ? "EntireNetwork"
00063:                 : result.StringResult;
00064: 
00065:             if (string.Equals(mode, "SelectedPath", StringComparison.OrdinalIgnoreCase))
00066:             {
00067:                 ExecuteSelectedPath(document);
00068:             }
00069:             else
00070:             {
00071:                 ExecuteEntireNetworks(document);
00072:             }
00073:         }
00074: 
00075:         private static void ExecuteEntireNetworks(Document document)
00076:         {
00077:             Editor editor = document.Editor;
00078:             Database database = document.Database;
00079: 
00080:             PromptSelectionResult selectionResult = editor.SelectImplied();
00081:             if (selectionResult.Status != PromptStatus.OK ||
00082:                 selectionResult.Value == null ||
00083:                 selectionResult.Value.Count == 0)
00084:             {
00085:                 var selectionOptions = new PromptSelectionOptions
00086:                 {
00087:                     MessageForAdding =
00088:                         "\nSelect one or more pipes/structures. CE Tools will expand each selection to its entire network: "
00089:                 };
00090:                 selectionResult = editor.GetSelection(selectionOptions);
00091:             }
00092: 
00093:             if (selectionResult.Status != PromptStatus.OK ||
00094:                 selectionResult.Value == null ||
00095:                 selectionResult.Value.Count == 0)
00096:             {
00097:                 return;
00098:             }
00099: 
00100:             var networkIds = new HashSet<ObjectId>();
00101:             int unsupportedSelections = 0;
00102: 
00103:             try
00104:             {
00105:                 using (Transaction transaction = database.TransactionManager.StartTransaction())
00106:                 {
00107:                     foreach (ObjectId selectedId in selectionResult.Value.GetObjectIds())
00108:                     {
00109:                         DBObject selectedObject = transaction.GetObject(
```

### Lines 809-889
```csharp
00809:                 {
00810:                     var pipe = (CivilPipe)transaction.GetObject(
00811:                         branch.PipeIds[index],
00812:                         OpenMode.ForWrite,
00813:                         false);
00814:                     SetCivilName(
00815:                         pipe,
00816:                         "P" +
00817:                         branch.BranchNumber.ToString(CultureInfo.InvariantCulture) +
00818:                         "." +
00819:                         (index + 1).ToString(CultureInfo.InvariantCulture));
00820:                     pipe.Description = branchName;
00821:                 }
00822:             }
00823:         }
00824: 
00825:         private static bool Confirm(Editor editor, string message)
00826:         {
00827:             var options = new PromptKeywordOptions(
00828:                 "\n" + message + "? [Yes/No] <No>: ")
00829:             {
00830:                 AllowNone = true
00831:             };
00832:             options.Keywords.Add("Yes");
00833:             options.Keywords.Add("No");
00834: 
00835:             PromptResult result = editor.GetKeywords(options);
00836:             return result.Status == PromptStatus.OK &&
00837:                    string.Equals(
00838:                        result.StringResult,
00839:                        "Yes",
00840:                        StringComparison.OrdinalIgnoreCase);
00841:         }
00842: 
00843:         private static void ExecuteSelectedPath(Document document)
00844:         {
00845:             Editor editor = document.Editor;
00846:             Database database = document.Database;
00847:             ObjectId labelledNetworkId = ObjectId.Null;
00848: 
00849:             PromptEntityResult startResult = PromptForStructure(
00850:                 editor,
00851:                 "\nSelect START manhole/structure: ");
00852:             if (startResult.Status != PromptStatus.OK)
00853:             {
00854:                 return;
00855:             }
00856: 
00857:             PromptEntityResult endResult = PromptForStructure(
00858:                 editor,
00859:                 "\nSelect END manhole/structure: ");
00860:             if (endResult.Status != PromptStatus.OK)
00861:             {
00862:                 return;
00863:             }
00864: 
00865:             if (startResult.ObjectId == endResult.ObjectId)
00866:             {
00867:                 editor.WriteMessage("\nStart and end structures must be different.");
00868:                 return;
00869:             }
00870: 
00871:             try
00872:             {
00873:                 using (Transaction transaction = database.TransactionManager.StartTransaction())
00874:                 {
00875:                     var startStructure = transaction.GetObject(
00876:                         startResult.ObjectId,
00877:                         OpenMode.ForRead,
00878:                         false) as CivilStructure;
00879:                     var endStructure = transaction.GetObject(
00880:                         endResult.ObjectId,
00881:                         OpenMode.ForRead,
00882:                         false) as CivilStructure;
00883: 
00884:                     if (startStructure == null || endStructure == null)
00885:                     {
00886:                         editor.WriteMessage(
00887:                             "\nBoth selected objects must be Civil 3D gravity-network structures.");
00888:                         return;
00889:                     }
```

## SpecialistModelExchangeCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 22-102
```csharp
00022:     /// Vendor-neutral exchange package and specialist-result import framework.
00023:     /// The package is intentionally open and auditable: CSV geometry, a JSON
00024:     /// manifest, explicit units/coordinate metadata and SHA-256 checksums.
00025:     /// It does not claim direct vendor API integration or certified model parity.
00026:     /// </summary>
00027:     public sealed class SpecialistModelExchangeCommands
00028:     {
00029:         private const string ResultRegApp = "CE_MODEL_RESULT_IMPORT";
00030:         private const string ResultLayerPrefix = "CE-MODEL-RESULT-";
00031:         private const int MaximumImportRows = 250000;
00032:         private const int MaximumExportVertices = 1000000;
00033: 
00034:         [CommandMethod("CE_TOOLS", "CE_MODELEXCHANGETOOLS", CommandFlags.Modal)]
00035:         public void ModelExchangeTools()
00036:         {
00037:             Document document = ActiveDocument();
00038:             if (document == null) return;
00039: 
00040:             var options = new PromptKeywordOptions(
00041:                 "\nSpecialist model exchange [Export/Template/Import/Info/Clear] <Export>: ")
00042:             {
00043:                 AllowNone = true
00044:             };
00045:             foreach (string keyword in new[] { "Export", "Template", "Import", "Info", "Clear" })
00046:                 options.Keywords.Add(keyword);
00047: 
00048:             PromptResult result = document.Editor.GetKeywords(options);
00049:             if (result.Status == PromptStatus.Cancel) return;
00050:             string choice = result.Status == PromptStatus.OK ? result.StringResult : "Export";
00051:             string command;
00052:             if (Equal(choice, "Template")) command = "CE_MODELRESULTTEMPLATE ";
00053:             else if (Equal(choice, "Import")) command = "CE_MODELRESULTIMPORT ";
00054:             else if (Equal(choice, "Info")) command = "CE_MODELRESULTINFO ";
00055:             else if (Equal(choice, "Clear")) command = "CE_MODELRESULTCLEAR ";
00056:             else command = "CE_MODELEXPORTPACKAGE ";
00057:             document.SendStringToExecute(command, true, false, true);
00058:         }
00059: 
00060:         [CommandMethod(
00061:             "CE_TOOLS",
00062:             "CE_MODELEXPORTPACKAGE",
00063:             CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
00064:         public void ExportPackage()
00065:         {
00066:             Document document = ActiveDocument();
00067:             if (document == null) return;
00068:             Editor editor = document.Editor;
00069: 
00070:             PromptSelectionResult selection = GetSelection(
00071:                 editor,
00072:                 "\nSelect geometry to include in the specialist-model exchange package: ");
00073:             if (selection.Status != PromptStatus.OK || selection.Value.Count == 0) return;
00074: 
00075:             double unitsPerMetre;
00076:             if (!PromptPositiveDouble(editor, "\nDrawing units per metre <1.0>: ", 1.0, out unitsPerMetre))
00077:                 return;
00078: 
00079:             double sampleSpacing;
00080:             if (!PromptPositiveDouble(
00081:                     editor,
00082:                     "\nMaximum curve sampling spacing in drawing units <5.0>: ",
00083:                     5.0,
00084:                     out sampleSpacing))
00085:                 return;
00086: 
00087:             string target;
00088:             if (!PromptTarget(editor, out target)) return;
00089: 
00090:             var saveOptions = new PromptSaveFileOptions(
00091:                 "\nChoose the exchange-package manifest path: ")
00092:             {
00093:                 Filter = "CE Model Exchange Manifest (*.json)|*.json",
00094:                 DialogCaption = "Create CE Tools Specialist Model Exchange Package",
00095:                 InitialFileName = "CE-Model-Exchange.json"
00096:             };
00097:             PromptFileNameResult fileResult = editor.GetFileNameForSave(saveOptions);
00098:             if (fileResult.Status != PromptStatus.OK) return;
00099: 
00100:             string manifestPath = EnsureExtension(fileResult.StringResult, ".json");
00101:             string folder = Path.GetDirectoryName(manifestPath) ?? Environment.CurrentDirectory;
00102:             string baseName = Path.GetFileNameWithoutExtension(manifestPath);
```

### Lines 297-376
```csharp
00297:                     EmptyAs(record.Scenario, "<Not supplied>"),
00298:                     EmptyAs(record.Time, "<Not supplied>"),
00299:                     record.X,
00300:                     record.Y,
00301:                     record.Z,
00302:                     FormatOptional(record.Depth, "m"),
00303:                     FormatOptional(record.Velocity, "m/s"),
00304:                     FormatOptional(record.WaterLevel, "m"),
00305:                     FormatOptional(record.HazardIndex, string.Empty));
00306:             }
00307:         }
00308: 
00309:         [CommandMethod("CE_TOOLS", "CE_MODELRESULTCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
00310:         public void ClearResults()
00311:         {
00312:             Document document = ActiveDocument();
00313:             if (document == null) return;
00314:             Editor editor = document.Editor;
00315:             var options = new PromptKeywordOptions(
00316:                 "\nErase all CE Tools imported specialist-result graphics [Yes/No] <No>: ")
00317:             {
00318:                 AllowNone = true
00319:             };
00320:             options.Keywords.Add("Yes");
00321:             options.Keywords.Add("No");
00322:             PromptResult result = editor.GetKeywords(options);
00323:             if (result.Status != PromptStatus.OK || !Equal(result.StringResult, "Yes"))
00324:             {
00325:                 editor.WriteMessage("\nCE_MODELRESULTCLEAR cancelled.");
00326:                 return;
00327:             }
00328: 
00329:             int erased = 0;
00330:             using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
00331:             {
00332:                 BlockTableRecord space = transaction.GetObject(
00333:                     document.Database.CurrentSpaceId,
00334:                     OpenMode.ForRead,
00335:                     false) as BlockTableRecord;
00336:                 if (space != null)
00337:                 {
00338:                     foreach (ObjectId id in space)
00339:                     {
00340:                         Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
00341:                         if (!HasResultRecord(entity)) continue;
00342:                         entity.UpgradeOpen();
00343:                         entity.Erase();
00344:                         erased++;
00345:                     }
00346:                 }
00347:                 transaction.Commit();
00348:             }
00349:             editor.Regen();
00350:             editor.WriteMessage(
00351:                 "\nCE_MODELRESULTCLEAR complete. Erased imported review graphics={0}. Source files and unrelated drawing objects were unchanged.",
00352:                 erased);
00353:         }
00354: 
00355:         private static List<ExchangeVertex> ReadSelectedGeometry(
00356:             Database database,
00357:             IEnumerable<ObjectId> ids,
00358:             double sampleSpacing)
00359:         {
00360:             var rows = new List<ExchangeVertex>();
00361:             int featureIndex = 0;
00362:             using (Transaction transaction = database.TransactionManager.StartTransaction())
00363:             {
00364:                 foreach (ObjectId id in ids)
00365:                 {
00366:                     Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
00367:                     if (entity == null || entity.IsErased) continue;
00368:                     featureIndex++;
00369:                     string featureId = "F" + featureIndex.ToString("D6", CultureInfo.InvariantCulture);
00370:                     Curve curve = entity as Curve;
00371:                     if (curve != null)
00372:                     {
00373:                         List<Point3d> points = SampleCurve(curve, sampleSpacing);
00374:                         for (int index = 0; index < points.Count; index++)
00375:                         {
00376:                             rows.Add(new ExchangeVertex(
```

### Lines 715-794
```csharp
00715:             if (category == "HAZARD-HIGH") return 1;
00716:             if (category == "HAZARD-MODERATE") return 30;
00717:             if (category == "DEPTH-1_00-PLUS") return 6;
00718:             if (category == "DEPTH-0_50-1_00") return 5;
00719:             if (category == "DEPTH-0_15-0_50") return 4;
00720:             if (category == "DEPTH-0_00-0_15") return 3;
00721:             return 8;
00722:         }
00723: 
00724:         private static double? HazardIndex(double? depth, double? velocity)
00725:         {
00726:             if (!depth.HasValue || !velocity.HasValue) return null;
00727:             if (!IsFinite(depth.Value) || !IsFinite(velocity.Value)) return null;
00728:             return depth.Value * (velocity.Value + 0.5);
00729:         }
00730: 
00731:         private static bool PromptTarget(Editor editor, out string target)
00732:         {
00733:             var options = new PromptKeywordOptions(
00734:                 "\nExchange target [Generic/HECRAS/InfraWorks/Twinmotion/Revit/Other] <Generic>: ")
00735:             {
00736:                 AllowNone = true
00737:             };
00738:             foreach (string keyword in new[] { "Generic", "HECRAS", "InfraWorks", "Twinmotion", "Revit", "Other" })
00739:                 options.Keywords.Add(keyword);
00740:             PromptResult result = editor.GetKeywords(options);
00741:             if (result.Status == PromptStatus.Cancel)
00742:             {
00743:                 target = string.Empty;
00744:                 return false;
00745:             }
00746:             target = result.Status == PromptStatus.OK ? result.StringResult : "Generic";
00747:             return true;
00748:         }
00749: 
00750:         private static bool PromptPositiveDouble(
00751:             Editor editor,
00752:             string message,
00753:             double defaultValue,
00754:             out double value)
00755:         {
00756:             var options = new PromptDoubleOptions(message)
00757:             {
00758:                 AllowNone = true,
00759:                 AllowNegative = false,
00760:                 AllowZero = false,
00761:                 DefaultValue = defaultValue
00762:             };
00763:             PromptDoubleResult result = editor.GetDouble(options);
00764:             if (result.Status == PromptStatus.Cancel)
00765:             {
00766:                 value = 0.0;
00767:                 return false;
00768:             }
00769:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00770:             return IsFinite(value) && value > 0.0;
00771:         }
00772: 
00773:         private static PromptSelectionResult GetSelection(Editor editor, string message)
00774:         {
00775:             PromptSelectionResult implied = editor.SelectImplied();
00776:             if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
00777:             {
00778:                 editor.SetImpliedSelection(new ObjectId[0]);
00779:                 return implied;
00780:             }
00781:             return editor.GetSelection(new PromptSelectionOptions
00782:             {
00783:                 MessageForAdding = message,
00784:                 AllowDuplicates = false,
00785:                 RejectObjectsFromNonCurrentSpace = true
00786:             });
00787:         }
00788: 
00789:         private static string ReadCoordinateSystemCode()
00790:         {
00791:             try
00792:             {
00793:                 object civilDocument = CivilApplication.ActiveDocument;
00794:                 if (civilDocument == null) return string.Empty;
```

## StandardQuantityTemplateCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 14-126
```csharp
00014: namespace CETools.Civil3D
00015: {
00016:     /// <summary>
00017:     /// Linked office quantity templates for parking/driveway and sidewalk works.
00018:     /// Source geometry and user-entered allowances are stored on the table and can
00019:     /// be refreshed. Template descriptions remain editable office standards and
00020:     /// require project-specific specification/measurement review before issue.
00021:     /// </summary>
00022:     public sealed class StandardQuantityTemplateCommands
00023:     {
00024:         private const string LinkRecordName = "CE_STANDARD_QUANTITY_TEMPLATE";
00025:         private const string SchemaVersion = "1";
00026: 
00027:         [CommandMethod("CE_TOOLS", "CE_STANDARDQTYTOOLS", CommandFlags.Modal)]
00028:         public void StandardQuantityTools()
00029:         {
00030:             Document document = ActiveDocument();
00031:             if (document == null) return;
00032:             var options = new PromptKeywordOptions(
00033:                 "\nStandard quantity tools [Create/Refresh/Export/Info] <Create>: ")
00034:             {
00035:                 AllowNone = true
00036:             };
00037:             options.Keywords.Add("Create");
00038:             options.Keywords.Add("Refresh");
00039:             options.Keywords.Add("Export");
00040:             options.Keywords.Add("Info");
00041:             PromptResult result = document.Editor.GetKeywords(options);
00042:             if (result.Status == PromptStatus.Cancel) return;
00043:             string choice = result.Status == PromptStatus.OK
00044:                 ? result.StringResult
00045:                 : "Create";
00046:             string command;
00047:             if (string.Equals(choice, "Refresh", StringComparison.OrdinalIgnoreCase))
00048:                 command = "CE_STANDARDQTYREFRESH ";
00049:             else if (string.Equals(choice, "Export", StringComparison.OrdinalIgnoreCase))
00050:                 command = "CE_STANDARDQTYEXPORT ";
00051:             else if (string.Equals(choice, "Info", StringComparison.OrdinalIgnoreCase))
00052:                 command = "CE_STANDARDQTYINFO ";
00053:             else
00054:                 command = "CE_STANDARDQTY ";
00055:             document.SendStringToExecute(command, true, false, true);
00056:         }
00057: 
00058:         [CommandMethod("CE_TOOLS", "CE_STANDARDQTY", CommandFlags.Modal | CommandFlags.Redraw)]
00059:         public void CreateStandardQuantitySchedule()
00060:         {
00061:             Document document = ActiveDocument();
00062:             if (document == null) return;
00063:             Editor editor = document.Editor;
00064: 
00065:             var templateOptions = new PromptKeywordOptions(
00066:                 "\nQuantity template [ParkingDriveway/Sidewalk] <ParkingDriveway>: ")
00067:             {
00068:                 AllowNone = true
00069:             };
00070:             templateOptions.Keywords.Add("ParkingDriveway");
00071:             templateOptions.Keywords.Add("Sidewalk");
00072:             PromptResult templateResult = editor.GetKeywords(templateOptions);
00073:             if (templateResult.Status == PromptStatus.Cancel) return;
00074:             StandardQuantityTemplate template =
00075:                 templateResult.Status == PromptStatus.OK &&
00076:                 string.Equals(templateResult.StringResult, "Sidewalk", StringComparison.OrdinalIgnoreCase)
00077:                     ? StandardQuantityTemplate.Sidewalk
00078:                     : StandardQuantityTemplate.ParkingDriveway;
00079: 
00080:             double unitsPerMetre;
00081:             if (!PromptPositiveDouble(editor, "Drawing units per metre", 1.0, out unitsPerMetre))
00082:                 return;
00083: 
00084:             List<string> areaHandles = PromptRequiredSelection(
00085:                 editor,
00086:                 "\nSelect closed parking/sidewalk area boundaries or supported area objects: ");
00087:             if (areaHandles.Count == 0)
00088:             {
00089:                 editor.WriteMessage(
00090:                     "\nCE_STANDARDQTY cancelled. No supported area sources were selected.");
00091:                 return;
00092:             }
00093: 
00094:             var categories = new Dictionary<string, List<string>>(
00095:                 StringComparer.OrdinalIgnoreCase);
00096:             if (template == StandardQuantityTemplate.ParkingDriveway)
00097:             {
00098:                 categories["Kerbs"] = PromptOptionalSelection(
00099:                     editor,
00100:                     "Select kerb linework for this schedule",
00101:                     "\nSelect kerb curves: ");
00102:                 categories["KerbsChannels"] = PromptOptionalSelection(
00103:                     editor,
00104:                     "Select kerb-and-channel linework for this schedule",
00105:                     "\nSelect kerb-and-channel curves: ");
00106:                 categories["VDrains"] = PromptOptionalSelection(
00107:                     editor,
00108:                     "Select V-drain linework for this schedule",
00109:                     "\nSelect V-drain curves: ");
00110:                 categories["Markings"] = PromptOptionalSelection(
00111:                     editor,
00112:                     "Select road-marking linework for this schedule",
00113:                     "\nSelect road-marking curves: ");
00114:             }
00115:             else
00116:             {
00117:                 categories["Kerbs"] = PromptOptionalSelection(
00118:                     editor,
00119:                     "Select sidewalk kerb/edge linework for this schedule",
00120:                     "\nSelect sidewalk kerb/edge curves: ");
00121:             }
00122: 
00123:             double cutVolume = 0.0;
00124:             double fillVolume = 0.0;
00125:             int signCount = 0;
00126:             if (template == StandardQuantityTemplate.ParkingDriveway)
```

### Lines 844-922
```csharp
00844: 
00845:         private static bool PromptNonNegativeInteger(Editor editor, string name, int defaultValue, out int value)
00846:         {
00847:             var options = new PromptIntegerOptions("\n" + name + " <" + defaultValue.ToString(CultureInfo.InvariantCulture) + ">: ")
00848:             {
00849:                 AllowNone = true,
00850:                 AllowNegative = false,
00851:                 AllowZero = true,
00852:                 DefaultValue = defaultValue,
00853:                 UseDefaultValue = true
00854:             };
00855:             PromptIntegerResult result = editor.GetInteger(options);
00856:             value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
00857:             return result.Status == PromptStatus.OK;
00858:         }
00859: 
00860:         private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
00861:         {
00862:             var options = new PromptKeywordOptions("\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
00863:             {
00864:                 AllowNone = true
00865:             };
00866:             options.Keywords.Add("Yes");
00867:             options.Keywords.Add("No");
00868:             PromptResult result = editor.GetKeywords(options);
00869:             if (result.Status == PromptStatus.Cancel) return false;
00870:             return result.Status == PromptStatus.None
00871:                 ? defaultValue
00872:                 : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
00873:         }
00874: 
00875:         private static double ParseDouble(IDictionary<string, string> values, string key, double fallback)
00876:         {
00877:             double value;
00878:             return double.TryParse(Value(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
00879:                 ? value
00880:                 : fallback;
00881:         }
00882: 
00883:         private static int ParseInteger(IDictionary<string, string> values, string key, int fallback)
00884:         {
00885:             int value;
00886:             return int.TryParse(Value(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
00887:                 ? value
00888:                 : fallback;
00889:         }
00890: 
00891:         private static string Value(IDictionary<string, string> values, string key)
00892:         {
00893:             string value;
00894:             return values.TryGetValue(key, out value) ? value : string.Empty;
00895:         }
00896: 
00897:         private static string FriendlyTemplate(StandardQuantityTemplate template)
00898:         {
00899:             return template == StandardQuantityTemplate.ParkingDriveway
00900:                 ? "Parking / Driveway"
00901:                 : "Sidewalk";
00902:         }
00903: 
00904:         private static double NormalizeHeight(double value)
00905:         {
00906:             if (Math.Abs(value - 1.8) < 0.05) return 1.8;
00907:             if (Math.Abs(value - 5.0) < 0.05) return 5.0;
00908:             return 2.0;
00909:         }
00910: 
00911:         private static KeyValuePair<string, string> Pair(string key, string value)
00912:         {
00913:             return new KeyValuePair<string, string>(key, value);
00914:         }
00915: 
00916:         private static Document ActiveDocument()
00917:         {
00918:             return AcApplication.DocumentManager.MdiActiveDocument;
00919:         }
00920:     }
00921: 
00922:     internal enum StandardQuantityTemplate
```

## StandardsSelectionCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 35-116
```csharp
00035:             "Approval Authority",
00036:             "Standards File",
00037:             "File Type",
00038:             "File Modified",
00039:             "File SHA-256",
00040:             "Notes",
00041:             "Selection Date"
00042:         };
00043: 
00044:         [CommandMethod("CE_TOOLS", "CE_STANDARDS", CommandFlags.Modal | CommandFlags.Redraw)]
00045:         public void StandardsMenu()
00046:         {
00047:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00048:             if (document == null)
00049:             {
00050:                 return;
00051:             }
00052: 
00053:             var options = new PromptKeywordOptions(
00054:                 "\nCE Standards Selection [Select/Info/Clear] <Select>: ")
00055:             {
00056:                 AllowNone = true
00057:             };
00058:             options.Keywords.Add("Select");
00059:             options.Keywords.Add("Info");
00060:             options.Keywords.Add("Clear");
00061: 
00062:             PromptResult result = document.Editor.GetKeywords(options);
00063:             if (result.Status == PromptStatus.Cancel)
00064:             {
00065:                 return;
00066:             }
00067: 
00068:             string mode = result.Status == PromptStatus.None
00069:                 ? "Select"
00070:                 : result.StringResult;
00071: 
00072:             if (string.Equals(mode, "Info", StringComparison.OrdinalIgnoreCase))
00073:             {
00074:                 ReportStandards(document);
00075:             }
00076:             else if (string.Equals(mode, "Clear", StringComparison.OrdinalIgnoreCase))
00077:             {
00078:                 ClearStandards(document);
00079:             }
00080:             else
00081:             {
00082:                 SelectStandards(document);
00083:             }
00084:         }
00085: 
00086:         [CommandMethod("CE_TOOLS", "CE_STANDARDSELECT", CommandFlags.Modal | CommandFlags.Redraw)]
00087:         public void StandardSelect()
00088:         {
00089:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00090:             if (document != null)
00091:             {
00092:                 SelectStandards(document);
00093:             }
00094:         }
00095: 
00096:         [CommandMethod("CE_TOOLS", "CE_STANDARDINFO", CommandFlags.Modal | CommandFlags.Redraw)]
00097:         public void StandardInfo()
00098:         {
00099:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00100:             if (document != null)
00101:             {
00102:                 ReportStandards(document);
00103:             }
00104:         }
00105: 
00106:         [CommandMethod("CE_TOOLS", "CE_STANDARDCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
00107:         public void StandardClear()
00108:         {
00109:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00110:             if (document != null)
00111:             {
00112:                 ClearStandards(document);
00113:             }
00114:         }
00115: 
00116:         private static void SelectStandards(Document document)
```

### Lines 353-461
```csharp
00353:         private static string ComputeSha256(string path)
00354:         {
00355:             using (FileStream stream = File.OpenRead(path))
00356:             using (SHA256 sha256 = SHA256.Create())
00357:             {
00358:                 byte[] hash = sha256.ComputeHash(stream);
00359:                 return BitConverter.ToString(hash).Replace("-", string.Empty);
00360:             }
00361:         }
00362: 
00363:         private static string PromptForRegion(Editor editor, string existing)
00364:         {
00365:             string defaultRegion = NormalizeRegion(existing);
00366:             if (string.IsNullOrWhiteSpace(defaultRegion))
00367:             {
00368:                 defaultRegion = "Namibia";
00369:             }
00370: 
00371:             var options = new PromptKeywordOptions(
00372:                 string.Format(
00373:                     "\nRegion / framework [Namibia/SouthAfrica/International/Custom] <{0}>: ",
00374:                     defaultRegion))
00375:             {
00376:                 AllowNone = true
00377:             };
00378:             options.Keywords.Add("Namibia");
00379:             options.Keywords.Add("SouthAfrica");
00380:             options.Keywords.Add("International");
00381:             options.Keywords.Add("Custom");
00382: 
00383:             PromptResult result = editor.GetKeywords(options);
00384:             if (result.Status == PromptStatus.Cancel)
00385:             {
00386:                 return null;
00387:             }
00388: 
00389:             return result.Status == PromptStatus.None
00390:                 ? defaultRegion
00391:                 : result.StringResult;
00392:         }
00393: 
00394:         private static string PromptForText(Editor editor, string field, string defaultValue)
00395:         {
00396:             string prompt = string.IsNullOrWhiteSpace(defaultValue)
00397:                 ? string.Format("\n{0}: ", field)
00398:                 : string.Format("\n{0} <{1}>: ", field, defaultValue);
00399: 
00400:             var options = new PromptStringOptions(prompt)
00401:             {
00402:                 AllowSpaces = true,
00403:                 UseDefaultValue = !string.IsNullOrWhiteSpace(defaultValue),
00404:                 DefaultValue = defaultValue ?? string.Empty
00405:             };
00406: 
00407:             PromptResult result = editor.GetString(options);
00408:             if (result.Status != PromptStatus.OK)
00409:             {
00410:                 return null;
00411:             }
00412: 
00413:             return (result.StringResult ?? string.Empty).Trim();
00414:         }
00415: 
00416:         private static StandardsMetadata ReadStandards(Database database)
00417:         {
00418:             var metadata = new StandardsMetadata();
00419: 
00420:             try
00421:             {
00422:                 using (Transaction transaction = database.TransactionManager.StartTransaction())
00423:                 {
00424:                     DBDictionary namedObjects = transaction.GetObject(
00425:                         database.NamedObjectsDictionaryId,
00426:                         OpenMode.ForRead,
00427:                         false) as DBDictionary;
00428:                     if (namedObjects == null || !namedObjects.Contains(RootDictionaryName))
00429:                     {
00430:                         return metadata;
00431:                     }
00432: 
00433:                     DBDictionary root = transaction.GetObject(
00434:                         namedObjects.GetAt(RootDictionaryName),
00435:                         OpenMode.ForRead,
00436:                         false) as DBDictionary;
00437:                     if (root == null || !root.Contains(StandardsRecordName))
00438:                     {
00439:                         return metadata;
00440:                     }
00441: 
00442:                     Xrecord record = transaction.GetObject(
00443:                         root.GetAt(StandardsRecordName),
00444:                         OpenMode.ForRead,
00445:                         false) as Xrecord;
00446:                     if (record == null || record.Data == null)
00447:                     {
00448:                         return metadata;
00449:                     }
00450: 
00451:                     ReadPairs(record.Data, metadata.Set);
00452:                     metadata.Exists = true;
00453:                 }
00454:             }
00455:             catch
00456:             {
00457:                 // A malformed or inaccessible record is treated as absent.
00458:             }
00459: 
00460:             return metadata;
00461:         }
```

## StormwaterProductionCommands.cs
Hits: `PromptStringOptions`, `GetString(`

### Lines 2054-2133
```csharp
02054:                 .Where(char.IsDigit)
02055:                 .ToArray());
02056:             int value;
02057:             return int.TryParse(
02058:                 digits,
02059:                 NumberStyles.None,
02060:                 CultureInfo.InvariantCulture,
02061:                 out value)
02062:                 ? value + 1
02063:                 : int.MaxValue;
02064:         }
02065: 
02066:         private static bool PromptText(
02067:             Editor editor,
02068:             string label,
02069:             string current,
02070:             out string value)
02071:         {
02072:             PromptStringOptions options = new PromptStringOptions(
02073:                 "\n" + label + " <" +
02074:                 DisplaySetting(current) +
02075:                 ">: ")
02076:             {
02077:                 AllowSpaces = true
02078:             };
02079:             PromptResult result = editor.GetString(options);
02080:             if (result.Status == PromptStatus.Cancel)
02081:             {
02082:                 value = current;
02083:                 return false;
02084:             }
02085: 
02086:             value = result.Status == PromptStatus.OK
02087:                 ? result.StringResult.Trim()
02088:                 : current;
02089:             return true;
02090:         }
02091: 
02092:         private static string DisplaySetting(string value)
02093:         {
02094:             return string.IsNullOrWhiteSpace(value)
02095:                 ? "first available"
02096:                 : value;
02097:         }
02098: 
02099:         private static bool Confirm(
02100:             Editor editor,
02101:             string message)
02102:         {
02103:             return DisciplineWorkflowDialogs.Confirm("CE Tools — Stormwater", message + "?");
02104:         }
02105: 
02106:         private sealed class StormwaterAlignmentPlan
02107:         {
02108:             public StormwaterAlignmentPlan(
02109:                 string branchKey,
02110:                 string sourceKind,
02111:                 IReadOnlyList<Point3d> planPoints,
02112:                 IEnumerable<string> sourceHandles,
02113:                 ObjectId sourcePolylineId)
02114:             {
02115:                 BranchKey = branchKey;
02116:                 SourceKind = sourceKind;
02117:                 PlanPoints = planPoints;
02118:                 SourceHandles = sourceHandles.ToList();
02119:                 SourcePolylineId = sourcePolylineId;
02120:             }
02121: 
02122:             public string BranchKey { get; }
02123:             public string SourceKind { get; }
02124:             public IReadOnlyList<Point3d> PlanPoints { get; }
02125:             public IReadOnlyList<string> SourceHandles { get; }
02126:             public ObjectId SourcePolylineId { get; }
02127:         }
02128: 
02129:         private sealed class StormwaterAlignmentRecord
02130:         {
02131:             public StormwaterAlignmentRecord(
02132:                 ObjectId alignmentId,
02133:                 string branchKey,
```

## StormwaterSequenceCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 70-150
```csharp
00070:                     out networkIds,
00071:                     out unsupported);
00072:             }
00073:             catch (System.Exception exception)
00074:             {
00075:                 editor.WriteMessage(
00076:                     "\nCE_SWSEQ cancelled while reading the selection: " +
00077:                     exception.Message);
00078:                 return;
00079:             }
00080: 
00081:             if (networkIds.Count == 0)
00082:             {
00083:                 editor.WriteMessage(
00084:                     "\nCE_SWSEQ: select at least one Civil 3D gravity-network pipe or structure.");
00085:                 return;
00086:             }
00087: 
00088:             PromptKeywordOptions modeOptions = new PromptKeywordOptions(
00089:                 "\nMain branch method [Automatic/SelectMain] <Automatic>: ")
00090:             {
00091:                 AllowNone = true
00092:             };
00093:             modeOptions.Keywords.Add("Automatic");
00094:             modeOptions.Keywords.Add("SelectMain");
00095: 
00096:             PromptResult modeResult = editor.GetKeywords(modeOptions);
00097:             if (modeResult.Status == PromptStatus.Cancel)
00098:                 return;
00099: 
00100:             bool selectMain =
00101:                 modeResult.Status == PromptStatus.OK &&
00102:                 modeResult.StringResult.Equals(
00103:                     "SelectMain",
00104:                     StringComparison.OrdinalIgnoreCase);
00105: 
00106:             ObjectId selectedStartId = ObjectId.Null;
00107:             ObjectId selectedEndId = ObjectId.Null;
00108:             if (selectMain)
00109:             {
00110:                 if (networkIds.Count != 1)
00111:                 {
00112:                     editor.WriteMessage(
00113:                         "\nCE_SWSEQ SelectMain supports one network at a time. " +
00114:                         "Use Automatic for multiple selected networks.");
00115:                     return;
00116:                 }
00117: 
00118:                 if (!PromptMainStructures(
00119:                         editor,
00120:                         database,
00121:                         networkIds[0],
00122:                         out selectedStartId,
00123:                         out selectedEndId))
00124:                     return;
00125:             }
00126: 
00127:             List<StormwaterNetworkPlan> plans;
00128:             try
00129:             {
00130:                 using (Transaction transaction =
00131:                     database.TransactionManager.StartTransaction())
00132:                 {
00133:                     plans = new List<StormwaterNetworkPlan>();
00134:                     foreach (ObjectId networkId in networkIds.OrderBy(id => id.Handle.Value))
00135:                     {
00136:                         StormwaterGraph graph =
00137:                             BuildGraph(networkId, transaction);
00138:                         StormwaterPath mainPath = selectMain
00139:                             ? FindPath(graph, selectedStartId, selectedEndId)
00140:                             : FindAutomaticMainPath(graph);
00141:                         OrientFromHighToLow(mainPath, graph);
00142:                         List<StormwaterPath> branches =
00143:                             ExtractBranches(graph, mainPath);
00144:                         plans.Add(new StormwaterNetworkPlan(
00145:                             graph,
00146:                             mainPath,
00147:                             branches));
00148:                     }
00149:                 }
00150:             }
```

### Lines 859-939
```csharp
00859:                     nodeSequence.ToString("00", CultureInfo.InvariantCulture);
00860:                 structure.Description =
00861:                     "CE stormwater | " + branchKey +
00862:                     " | Node " +
00863:                     nodeSequence.ToString("00", CultureInfo.InvariantCulture);
00864:                 StormwaterMetadata.WriteTag(
00865:                     structure,
00866:                     new StormwaterPartTag(
00867:                         networkHandle,
00868:                         branchKey,
00869:                         nodeSequence,
00870:                         "Structure"));
00871:                 nodeSequence++;
00872:             }
00873:         }
00874: 
00875:         private static bool Confirm(Editor editor, string message)
00876:         {
00877:             PromptKeywordOptions options = new PromptKeywordOptions(
00878:                 "\n" + message + "? [Yes/No] <No>: ")
00879:             {
00880:                 AllowNone = true
00881:             };
00882:             options.Keywords.Add("Yes");
00883:             options.Keywords.Add("No");
00884: 
00885:             PromptResult result = editor.GetKeywords(options);
00886:             return result.Status == PromptStatus.OK &&
00887:                    result.StringResult.Equals(
00888:                        "Yes",
00889:                        StringComparison.OrdinalIgnoreCase);
00890:         }
00891: 
00892:         private sealed class StormwaterNetworkPlan
00893:         {
00894:             public StormwaterNetworkPlan(
00895:                 StormwaterGraph graph,
00896:                 StormwaterPath mainPath,
00897:                 IReadOnlyList<StormwaterPath> branches)
00898:             {
00899:                 Graph = graph;
00900:                 MainPath = mainPath;
00901:                 Branches = branches;
00902:             }
00903: 
00904:             public StormwaterGraph Graph { get; }
00905:             public StormwaterPath MainPath { get; }
00906:             public IReadOnlyList<StormwaterPath> Branches { get; }
00907:         }
00908: 
00909:         private sealed class BranchCandidate
00910:         {
00911:             public BranchCandidate(
00912:                 ObjectId rootId,
00913:                 int rootOrder,
00914:                 StormwaterPath path)
00915:             {
00916:                 RootId = rootId;
00917:                 RootOrder = rootOrder;
00918:                 Path = path;
00919:             }
00920: 
00921:             public ObjectId RootId { get; }
00922:             public int RootOrder { get; }
00923:             public StormwaterPath Path { get; }
00924:         }
00925:     }
00926: 
00927:     internal sealed class StormwaterGraph
00928:     {
00929:         public StormwaterGraph(
00930:             ObjectId networkId,
00931:             string networkName)
00932:         {
00933:             NetworkId = networkId;
00934:             NetworkName = networkName ?? string.Empty;
00935:             Nodes = new Dictionary<ObjectId, StormwaterNode>();
00936:             Edges = new List<StormwaterEdge>();
00937:         }
00938: 
00939:         public ObjectId NetworkId { get; }
```

## SurfaceCorrectionCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 32-115
```csharp
00032:         private const string SimplifiedSuffix = " - CE SIMPLIFIED";
00033:         private const double GeometryTolerance = 1e-9;
00034: 
00035:         private static readonly string[] ContaminationKeywords =
00036:         {
00037:             "BUILDING", "HOUSE", "ROOF", "TREE", "VEGETATION", "POLE",
00038:             "LIGHT", "SIGN", "OVERHEAD", "OHL", "POWER", "MANHOLE",
00039:             "MH", "INVERT", "SEWER", "STORM", "VALVE", "HYDRANT",
00040:             "STRUCTURE", "CHAMBER", "TANK"
00041:         };
00042: 
00043:         [CommandMethod("CE_SURFCTOOLS", CommandFlags.Modal)]
00044:         public void SurfaceCorrectionTools()
00045:         {
00046:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00047:             if (document == null)
00048:                 return;
00049: 
00050:             var options = new PromptKeywordOptions(
00051:                 "\nSurface correction tools [Audit/Correct/Simplify/Restore/Settings/Info] <Audit>: ")
00052:             {
00053:                 AllowNone = true
00054:             };
00055:             foreach (string keyword in new[]
00056:             {
00057:                 "Audit", "Correct", "Simplify", "Restore", "Settings", "Info"
00058:             })
00059:                 options.Keywords.Add(keyword);
00060: 
00061:             PromptResult result = document.Editor.GetKeywords(options);
00062:             if (result.Status == PromptStatus.Cancel)
00063:                 return;
00064: 
00065:             string choice = result.Status == PromptStatus.OK
00066:                 ? result.StringResult
00067:                 : "Audit";
00068:             string command;
00069:             if (choice.Equals("Correct", StringComparison.OrdinalIgnoreCase))
00070:                 command = "CE_SURFCORRECT ";
00071:             else if (choice.Equals("Simplify", StringComparison.OrdinalIgnoreCase))
00072:                 command = "CE_SURFSIMPLIFY ";
00073:             else if (choice.Equals("Restore", StringComparison.OrdinalIgnoreCase))
00074:                 command = "CE_SURFCRESTORE ";
00075:             else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
00076:                 command = "CE_SURFCSETTINGS ";
00077:             else if (choice.Equals("Info", StringComparison.OrdinalIgnoreCase))
00078:                 command = "CE_SURFCINFO ";
00079:             else
00080:                 command = "CE_SURFAUDIT ";
00081: 
00082:             document.SendStringToExecute(command, true, false, true);
00083:         }
00084: 
00085:         [CommandMethod("CE_SURFCSETTINGS", CommandFlags.Modal)]
00086:         public void ConfigureSettings()
00087:         {
00088:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00089:             if (document == null)
00090:                 return;
00091: 
00092:             Editor editor = document.Editor;
00093:             CorrectionSettings settings = CorrectionSettings.Read(document.Database);
00094: 
00095:             if (!PromptNonNegativeDouble(editor, "Zero-elevation tolerance", settings.ZeroTolerance, out settings.ZeroTolerance))
00096:                 return;
00097:             if (!PromptPositiveDouble(editor, "Local spike/low-point tolerance", settings.SpikeTolerance, out settings.SpikeTolerance))
00098:                 return;
00099:             if (!PromptPositiveDouble(editor, "Neighbour search radius", settings.NeighbourRadius, out settings.NeighbourRadius))
00100:                 return;
00101:             if (!PromptPositiveInteger(editor, "Minimum neighbours", settings.MinimumNeighbours, out settings.MinimumNeighbours))
00102:                 return;
00103:             if (!PromptPositiveDouble(editor, "Contamination search radius", settings.ContaminationRadius, out settings.ContaminationRadius))
00104:                 return;
00105:             if (!PromptPositiveInteger(editor, "Maximum audit vertices", settings.MaximumAuditVertices, out settings.MaximumAuditVertices))
00106:                 return;
00107:             if (!PromptPositiveDouble(editor, "Default simplification grid size", settings.SimplificationGrid, out settings.SimplificationGrid))
00108:                 return;
00109:             if (!PromptPositiveInteger(editor, "Maximum report rows", settings.MaximumReportRows, out settings.MaximumReportRows))
00110:                 return;
00111: 
00112:             settings.Write(document.Database);
00113:             editor.WriteMessage("\nCE_SURFCSETTINGS saved in the current DWG.");
00114:         }
00115: 
```

### Lines 156-235
```csharp
00156:             SurfaceAudit audit;
00157:             try
00158:             {
00159:                 audit = AnalyseSurface(document.Database, surfaceId, settings);
00160:             }
00161:             catch (System.Exception exception)
00162:             {
00163:                 document.Editor.WriteMessage("\nCE_SURFCORRECT cancelled. " + exception.Message);
00164:                 return;
00165:             }
00166: 
00167:             WriteAuditSummary(document.Editor, audit);
00168:             if (audit.Vertices.Count == 0)
00169:             {
00170:                 document.Editor.WriteMessage("\nNo readable surface vertices were found.");
00171:                 return;
00172:             }
00173: 
00174:             PromptKeywordOptions contaminationOptions = new PromptKeywordOptions(
00175:                 "\nContamination handling [Keep/Exclude] <Keep>: ")
00176:             {
00177:                 AllowNone = true
00178:             };
00179:             contaminationOptions.Keywords.Add("Keep");
00180:             contaminationOptions.Keywords.Add("Exclude");
00181:             PromptResult contaminationResult = document.Editor.GetKeywords(contaminationOptions);
00182:             if (contaminationResult.Status == PromptStatus.Cancel)
00183:                 return;
00184:             bool excludeContamination = contaminationResult.Status == PromptStatus.OK &&
00185:                 contaminationResult.StringResult.Equals("Exclude", StringComparison.OrdinalIgnoreCase);
00186: 
00187:             List<Point3d> corrected = BuildCorrectedPoints(
00188:                 audit,
00189:                 excludeContamination,
00190:                 settings);
00191:             int replaced = audit.Issues.Count(issue =>
00192:                 issue.Kind == IssueKind.ZeroElevation ||
00193:                 issue.Kind == IssueKind.LocalSpike ||
00194:                 issue.Kind == IssueKind.LocalLow);
00195:             int excluded = excludeContamination
00196:                 ? audit.Issues.Where(issue => issue.Kind == IssueKind.Contamination)
00197:                     .Select(issue => issue.VertexIndex)
00198:                     .Distinct()
00199:                     .Count()
00200:                 : 0;
00201: 
00202:             document.Editor.WriteMessage(
00203:                 "\nCE_SURFCORRECT preview: source vertices={0}; corrected output vertices={1}; replacement candidates={2}; excluded contamination candidates={3}.",
00204:                 audit.Vertices.Count,
00205:                 corrected.Count,
00206:                 replaced,
00207:                 excluded);
00208:             document.Editor.WriteMessage(
00209:                 "\nThe source surface will remain unchanged. A separate CE corrected surface will be created.");
00210: 
00211:             if (!Confirm(document.Editor, "Create the reversible corrected surface copy"))
00212:                 return;
00213: 
00214:             try
00215:             {
00216:                 string generatedName;
00217:                 ObjectId generatedId = CreateGeneratedSurface(
00218:                     document.Database,
00219:                     civilDocument,
00220:                     audit,
00221:                     corrected,
00222:                     "Corrected",
00223:                     CorrectedSuffix,
00224:                     settings,
00225:                     out generatedName);
00226:                 document.Editor.WriteMessage(
00227:                     "\nCE_SURFCORRECT complete. Created '{0}' ({1}). Original surface '{2}' was not modified.",
00228:                     generatedName,
00229:                     generatedId.Handle,
00230:                     audit.SurfaceName);
00231:             }
00232:             catch (System.Exception exception)
00233:             {
00234:                 document.Editor.WriteMessage(
00235:                     "\nCE_SURFCORRECT cancelled. No generated surface was committed. " +
```

### Lines 1315-1394
```csharp
01315:             int current,
01316:             out int value)
01317:         {
01318:             var options = new PromptIntegerOptions(
01319:                 "\n" + label + " <" + current.ToString(CultureInfo.InvariantCulture) + ">: ")
01320:             {
01321:                 AllowNegative = false,
01322:                 AllowZero = false,
01323:                 UseDefaultValue = true,
01324:                 DefaultValue = current
01325:             };
01326:             PromptIntegerResult result = editor.GetInteger(options);
01327:             value = result.Status == PromptStatus.OK ? result.Value : current;
01328:             return result.Status == PromptStatus.OK;
01329:         }
01330: 
01331:         private static bool Confirm(Editor editor, string message)
01332:         {
01333:             var options = new PromptKeywordOptions(
01334:                 "\n" + message + "? [Yes/No] <No>: ")
01335:             {
01336:                 AllowNone = true
01337:             };
01338:             options.Keywords.Add("Yes");
01339:             options.Keywords.Add("No");
01340:             PromptResult result = editor.GetKeywords(options);
01341:             return result.Status == PromptStatus.OK &&
01342:                    result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
01343:         }
01344: 
01345:         private static void EnsureRegApp(Database database, Transaction transaction)
01346:         {
01347:             RegAppTable table = (RegAppTable)transaction.GetObject(
01348:                 database.RegAppTableId,
01349:                 OpenMode.ForRead,
01350:                 false);
01351:             if (table.Has(RegAppName))
01352:                 return;
01353:             table.UpgradeOpen();
01354:             var record = new RegAppTableRecord { Name = RegAppName };
01355:             table.Add(record);
01356:             transaction.AddNewlyCreatedDBObject(record, true);
01357:         }
01358: 
01359:         private static ResultBuffer BuildTag(
01360:             string generatedType,
01361:             string sourceHandle,
01362:             string settingsText)
01363:         {
01364:             return new ResultBuffer(
01365:                 new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
01366:                 new TypedValue((int)DxfCode.ExtendedDataAsciiString, generatedType ?? string.Empty),
01367:                 new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceHandle ?? string.Empty),
01368:                 new TypedValue((int)DxfCode.ExtendedDataAsciiString, settingsText ?? string.Empty));
01369:         }
01370: 
01371:         private static bool TryReadTag(
01372:             DBObject item,
01373:             out string generatedType,
01374:             out string sourceHandle,
01375:             out string settingsText)
01376:         {
01377:             generatedType = sourceHandle = settingsText = string.Empty;
01378:             using (ResultBuffer data = item.GetXDataForApplication(RegAppName))
01379:             {
01380:                 if (data == null)
01381:                     return false;
01382:                 string[] values = data.AsArray()
01383:                     .Where(value => value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
01384:                     .Select(value => value.Value as string)
01385:                     .Where(value => value != null)
01386:                     .ToArray();
01387:                 if (values.Length < 3)
01388:                     return false;
01389:                 generatedType = values[0];
01390:                 sourceHandle = values[1];
01391:                 settingsText = values[2];
01392:                 return generatedType == "Corrected" || generatedType == "Simplified";
01393:             }
01394:         }
```

## SurfaceHydrologyCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 41-120
```csharp
00041:                 "Run preliminary surface-flow, catchment and pre/post hydrograph workflows.",
00042:                 new List<DisciplineWorkflowAction>
00043:                 {
00044:                     new DisciplineWorkflowAction("Surface flow route", "CE_SURFACEFLOW", "Trace a route over a sampled Civil 3D surface.", "01 Terrain"),
00045:                     new DisciplineWorkflowAction("Delineate catchment", "CE_CATCHMENTDELINEATE", "Derive a preliminary outlet catchment from the sampled surface.", "01 Terrain"),
00046:                     new DisciplineWorkflowAction("Compare hydrographs", "CE_HYDROGRAPHCOMPARE", "Review pre- and post-development hydrograph inputs.", "02 Hydrology"),
00047:                     new DisciplineWorkflowAction("Clear hydrology graphics", "CE_HYDROLOGYCLEAR", "Remove CE Tools hydrology review graphics.", "03 Cleanup")
00048:                 });
00049:         }
00050: 
00051:         [CommandMethod("CE_TOOLS", "CE_SURFACEFLOW", CommandFlags.Modal | CommandFlags.Redraw)]
00052:         public void SurfaceFlow()
00053:         {
00054:             Document document = ActiveDocument();
00055:             if (document == null) return;
00056:             HydrologyCivilInput input;
00057:             if (!PromptAnalysisInput(document, out input)) return;
00058: 
00059:             var modeOptions = new PromptKeywordOptions(
00060:                 "\nFlow-route start [Pick/MaximumAccumulation] <MaximumAccumulation>: ")
00061:             {
00062:                 AllowNone = true
00063:             };
00064:             modeOptions.Keywords.Add("Pick");
00065:             modeOptions.Keywords.Add("MaximumAccumulation");
00066:             PromptResult modeResult = document.Editor.GetKeywords(modeOptions);
00067:             if (modeResult.Status == PromptStatus.Cancel) return;
00068: 
00069:             try
00070:             {
00071:                 HydrologySample sample = SampleAndAnalyse(document.Database, input);
00072:                 int start;
00073:                 if (modeResult.Status == PromptStatus.OK && Equal(modeResult.StringResult, "Pick"))
00074:                 {
00075:                     PromptPointResult pointResult = document.Editor.GetPoint(
00076:                         "\nPick a point near the desired flow-route start: ");
00077:                     if (pointResult.Status != PromptStatus.OK) return;
00078:                     Point3d point = pointResult.Value.TransformBy(
00079:                         document.Editor.CurrentUserCoordinateSystem);
00080:                     start = FindNearestActiveCell(sample, point);
00081:                 }
00082:                 else
00083:                 {
00084:                     start = sample.Analysis.FindMaximumAccumulationCell();
00085:                 }
00086:                 if (start < 0)
00087:                 {
00088:                     document.Editor.WriteMessage(
00089:                         "\nCE_SURFACEFLOW stopped. No active grid cell could be selected.");
00090:                     return;
00091:                 }
00092: 
00093:                 IReadOnlyList<GridCell> route = sample.Analysis.TraceRoute(start);
00094:                 FlowRouteSummary summary = SummariseRoute(sample, route, input.UnitsPerMetre);
00095:                 var review = new List<KeyValuePair<string, string>>
00096:                 {
00097:                     Pair("Surface", input.SurfaceName),
00098:                     Pair("Grid rows x columns", sample.Rows + " x " + sample.Columns),
00099:                     Pair("Active sampled cells", sample.ActiveCount.ToString(CultureInfo.InvariantCulture)),
00100:                     Pair("Grid spacing", input.Spacing.ToString("N3", CultureInfo.CurrentCulture)),
00101:                     Pair("Filled depression cells", sample.FilledCellCount.ToString(CultureInfo.InvariantCulture)),
00102:                     Pair("Maximum fill depth", sample.MaximumFillDepth.ToString("N3", CultureInfo.CurrentCulture)),
00103:                     Pair("Route cells", route.Count.ToString(CultureInfo.InvariantCulture)),
00104:                     Pair("Route length", summary.LengthMetres.ToString("N2", CultureInfo.CurrentCulture) + " m"),
00105:                     Pair("Contributing area at route start", summary.StartAreaHectares.ToString("N3", CultureInfo.CurrentCulture) + " ha"),
00106:                     Pair("Route outlet", FormatPoint(summary.OutletPoint)),
00107:                     Pair("Model status", "Regular-grid D8 screening — not a calibrated 1D/2D flood model")
00108:                 };
00109:                 if (!PopupTablePresenter.ShowReview(
00110:                         "CE Tools - Surface Flow Route",
00111:                         "The selected Civil 3D surface and boundary remain unchanged. Only removable CE review graphics will be created.",
00112:                         review,
00113:                         "Create Flow Review"))
00114:                     return;
00115: 
00116:                 int generated = CreateFlowGraphics(
00117:                     document.Database,
00118:                     input,
00119:                     sample,
00120:                     route,
```

### Lines 1060-1140
```csharp
01060:             string label,
01061:             double defaultValue,
01062:             out double value)
01063:         {
01064:             if (!PromptPositiveDouble(editor, label, defaultValue, out value))
01065:                 return false;
01066:             if (value <= 1.0) return true;
01067:             editor.WriteMessage(
01068:                 "\n{0} must be greater than zero and no more than 1.0.",
01069:                 label);
01070:             return false;
01071:         }
01072: 
01073:         private static bool PromptYesNo(
01074:             Editor editor,
01075:             string question,
01076:             bool defaultYes)
01077:         {
01078:             var options = new PromptKeywordOptions(
01079:                 "\n" + question + " [Yes/No] <" +
01080:                 (defaultYes ? "Yes" : "No") + ">: ")
01081:             {
01082:                 AllowNone = true
01083:             };
01084:             options.Keywords.Add("Yes");
01085:             options.Keywords.Add("No");
01086:             PromptResult result = editor.GetKeywords(options);
01087:             if (result.Status == PromptStatus.Cancel) return false;
01088:             return result.Status == PromptStatus.None
01089:                 ? defaultYes
01090:                 : Equal(result.StringResult, "Yes");
01091:         }
01092: 
01093:         private static bool PromptExcelPath(
01094:             Editor editor,
01095:             string defaultName,
01096:             out string path)
01097:         {
01098:             path = string.Empty;
01099:             var options = new PromptSaveFileOptions(
01100:                 "\nChoose the hydrograph Excel workbook path: ")
01101:             {
01102:                 DialogCaption = "Export CE Tools Hydrograph Review",
01103:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
01104:                 InitialFileName = defaultName
01105:             };
01106:             PromptFileNameResult result = editor.GetFileNameForSave(options);
01107:             if (result.Status != PromptStatus.OK) return false;
01108:             path = result.StringResult;
01109:             if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
01110:                 path += ".xlsx";
01111:             return true;
01112:         }
01113: 
01114:         private static string CellName(GridCell cell)
01115:         {
01116:             return "R" + cell.Row.ToString(CultureInfo.InvariantCulture) +
01117:                    " C" + cell.Column.ToString(CultureInfo.InvariantCulture);
01118:         }
01119: 
01120:         private static string FormatPoint(Point3d point)
01121:         {
01122:             return string.Format(
01123:                 CultureInfo.CurrentCulture,
01124:                 "X {0:N3}; Y {1:N3}; Z {2:N3}",
01125:                 point.X,
01126:                 point.Y,
01127:                 point.Z);
01128:         }
01129: 
01130:         private static bool Equal(string first, string second)
01131:         {
01132:             return string.Equals(
01133:                 first,
01134:                 second,
01135:                 StringComparison.OrdinalIgnoreCase);
01136:         }
01137: 
01138:         private static KeyValuePair<string, string> Pair(
01139:             string key,
01140:             string value)
```

## SurfacePondingCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 508-588
```csharp
00508:             PromptDoubleResult result = editor.GetDouble(options);
00509:             if (result.Status == PromptStatus.Cancel)
00510:             {
00511:                 value = defaultValue;
00512:                 return false;
00513:             }
00514:             value = result.Status == PromptStatus.OK
00515:                 ? result.Value
00516:                 : defaultValue;
00517:             return result.Status == PromptStatus.OK ||
00518:                    result.Status == PromptStatus.None;
00519:         }
00520: 
00521:         private static bool PromptYesNo(
00522:             Editor editor,
00523:             string question,
00524:             bool defaultYes)
00525:         {
00526:             var options = new PromptKeywordOptions(
00527:                 "\n" + question + " [Yes/No] <" +
00528:                 (defaultYes ? "Yes" : "No") + ">: ")
00529:             {
00530:                 AllowNone = true
00531:             };
00532:             options.Keywords.Add("Yes");
00533:             options.Keywords.Add("No");
00534:             PromptResult result = editor.GetKeywords(options);
00535:             if (result.Status == PromptStatus.Cancel) return false;
00536:             return result.Status == PromptStatus.None
00537:                 ? defaultYes
00538:                 : string.Equals(
00539:                     result.StringResult,
00540:                     "Yes",
00541:                     StringComparison.OrdinalIgnoreCase);
00542:         }
00543: 
00544:         private static bool PromptExcelPath(
00545:             Editor editor,
00546:             string defaultName,
00547:             out string path)
00548:         {
00549:             path = string.Empty;
00550:             var options = new PromptSaveFileOptions(
00551:                 "\nChoose the depression-storage Excel workbook path: ")
00552:             {
00553:                 DialogCaption = "Export CE Tools Depression Storage Review",
00554:                 Filter = "Excel Workbook (*.xlsx)|*.xlsx",
00555:                 InitialFileName = defaultName
00556:             };
00557:             PromptFileNameResult result = editor.GetFileNameForSave(options);
00558:             if (result.Status != PromptStatus.OK) return false;
00559:             path = result.StringResult;
00560:             if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
00561:                 path += ".xlsx";
00562:             return true;
00563:         }
00564: 
00565:         private static KeyValuePair<string, string> Pair(
00566:             string key,
00567:             string value)
00568:         {
00569:             return new KeyValuePair<string, string>(key, value);
00570:         }
00571: 
00572:         private static Document ActiveDocument()
00573:         {
00574:             return AcApplication.DocumentManager.MdiActiveDocument;
00575:         }
00576:     }
00577: 
00578:     internal sealed class PondingZone
00579:     {
00580:         public PondingZone(
00581:             int zoneNumber,
00582:             IList<int> cellIndices,
00583:             double areaHectares,
00584:             double storageCubicMetres,
00585:             double maximumDepthMetres,
00586:             int deepestCellIndex)
00587:         {
00588:             ZoneNumber = zoneNumber;
```

## SurfaceSpikeHoleRepairCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 889-968
```csharp
00889:             var options = new PromptIntegerOptions(
00890:                 "\n" + label + " <" + defaultValue.ToString(
00891:                     CultureInfo.InvariantCulture) + ">: ")
00892:             {
00893:                 AllowNegative = false,
00894:                 AllowZero = false,
00895:                 UseDefaultValue = true,
00896:                 DefaultValue = defaultValue
00897:             };
00898:             PromptIntegerResult result = editor.GetInteger(options);
00899:             value = result.Status == PromptStatus.OK
00900:                 ? result.Value
00901:                 : defaultValue;
00902:             return result.Status == PromptStatus.OK;
00903:         }
00904: 
00905:         private static bool Confirm(Editor editor, string message)
00906:         {
00907:             var options = new PromptKeywordOptions(
00908:                 "\n" + message + "? [Yes/No] <No>: ")
00909:             {
00910:                 AllowNone = true
00911:             };
00912:             options.Keywords.Add("Yes");
00913:             options.Keywords.Add("No");
00914:             PromptResult result = editor.GetKeywords(options);
00915:             return result.Status == PromptStatus.OK &&
00916:                    string.Equals(
00917:                        result.StringResult,
00918:                        "Yes",
00919:                        StringComparison.OrdinalIgnoreCase);
00920:         }
00921: 
00922:         private static void EnsureRegApp(
00923:             Database database,
00924:             Transaction transaction)
00925:         {
00926:             RegAppTable table = transaction.GetObject(
00927:                 database.RegAppTableId,
00928:                 OpenMode.ForRead,
00929:                 false) as RegAppTable;
00930:             if (table == null || table.Has(RegAppName)) return;
00931:             table.UpgradeOpen();
00932:             var record = new RegAppTableRecord { Name = RegAppName };
00933:             table.Add(record);
00934:             transaction.AddNewlyCreatedDBObject(record, true);
00935:         }
00936: 
00937:         private static string ReadName(DBObject item)
00938:         {
00939:             string name = Convert.ToString(
00940:                 ReadProperty(item, "Name"),
00941:                 CultureInfo.InvariantCulture);
00942:             return string.IsNullOrWhiteSpace(name)
00943:                 ? item.GetType().Name + " " + item.ObjectId.Handle
00944:                 : name;
00945:         }
00946: 
00947:         private static string UniqueName(string preferred, ISet<string> existing)
00948:         {
00949:             string candidate = preferred;
00950:             int suffix = 2;
00951:             while (existing.Contains(candidate))
00952:             {
00953:                 candidate = preferred + " (" +
00954:                     suffix.ToString(CultureInfo.InvariantCulture) + ")";
00955:                 suffix++;
00956:             }
00957:             return candidate;
00958:         }
00959: 
00960:         private sealed class RepairPlan
00961:         {
00962:             public RepairPlan(
00963:                 ObjectId sourceId,
00964:                 string sourceHandle,
00965:                 string sourceName,
00966:                 List<Point3d> sourceVertices,
00967:                 List<TriangleRecord> triangles,
00968:                 Dictionary<int, double> replacements,
```

## SurveyCorrectionComparisonCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 153-232
```csharp
00153:             {
00154:                 document.Editor.WriteMessage(
00155:                     "\nOriginal and corrected surfaces must be different.");
00156:                 return false;
00157:             }
00158: 
00159:             var toleranceOptions = new PromptDoubleOptions(
00160:                 "\nMinimum absolute elevation change to flag <0.001>: ")
00161:             {
00162:                 AllowNegative = false,
00163:                 AllowZero = true,
00164:                 UseDefaultValue = true,
00165:                 DefaultValue = 0.001
00166:             };
00167:             PromptDoubleResult toleranceResult =
00168:                 document.Editor.GetDouble(toleranceOptions);
00169:             if (toleranceResult.Status != PromptStatus.OK) return false;
00170: 
00171:             var modeOptions = new PromptKeywordOptions(
00172:                 "\nReport rows [ChangedOnly/AllSampled] <ChangedOnly>: ")
00173:             {
00174:                 AllowNone = true
00175:             };
00176:             modeOptions.Keywords.Add("ChangedOnly");
00177:             modeOptions.Keywords.Add("AllSampled");
00178:             PromptResult modeResult = document.Editor.GetKeywords(modeOptions);
00179:             if (modeResult.Status == PromptStatus.Cancel) return false;
00180:             bool changedOnly = modeResult.Status != PromptStatus.OK ||
00181:                 !string.Equals(
00182:                     modeResult.StringResult,
00183:                     "AllSampled",
00184:                     StringComparison.OrdinalIgnoreCase);
00185: 
00186:             request = new ComparisonRequest(
00187:                 originalId,
00188:                 correctedId,
00189:                 toleranceResult.Value,
00190:                 changedOnly);
00191:             return true;
00192:         }
00193: 
00194:         private static ComparisonResult Compare(
00195:             Database database,
00196:             ComparisonRequest request)
00197:         {
00198:             using (Transaction transaction =
00199:                 database.TransactionManager.StartTransaction())
00200:             {
00201:                 CivilSurface original = transaction.GetObject(
00202:                     request.OriginalSurfaceId,
00203:                     OpenMode.ForRead,
00204:                     false) as CivilSurface;
00205:                 CivilSurface corrected = transaction.GetObject(
00206:                     request.CorrectedSurfaceId,
00207:                     OpenMode.ForRead,
00208:                     false) as CivilSurface;
00209:                 if (original == null || corrected == null)
00210:                     throw new InvalidOperationException(
00211:                         "Both selected objects must be Civil 3D surfaces.");
00212: 
00213:                 List<Point3d> sourcePoints = ReadSurfaceVertices(original);
00214:                 int sourceCount = sourcePoints.Count;
00215:                 if (sourceCount == 0)
00216:                     throw new InvalidOperationException(
00217:                         "The original surface exposes no readable vertices.");
00218:                 if (sourceCount > MaximumSamplePoints)
00219:                 {
00220:                     int step = Math.Max(
00221:                         1,
00222:                         (int)Math.Ceiling(
00223:                             sourceCount / (double)MaximumSamplePoints));
00224:                     sourcePoints = sourcePoints
00225:                         .Where((point, index) => index % step == 0)
00226:                         .Take(MaximumSamplePoints)
00227:                         .ToList();
00228:                 }
00229: 
00230:                 var rows = new List<ComparisonRow>();
00231:                 int outside = 0;
00232:                 for (int index = 0; index < sourcePoints.Count; index++)
```

## TypicalDetailsCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 31-225
```csharp
00031:             "Stormwater",
00032:             "Sewer",
00033:             "Water",
00034:             "Earthworks",
00035:             "Parking",
00036:             "Landscaping",
00037:             "Structures",
00038:             "Standard Construction Notes",
00039:             "General Details"
00040:         };
00041: 
00042:         [CommandMethod("CE_DETAILTOOLS", CommandFlags.Modal)]
00043:         public void DetailTools()
00044:         {
00045:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00046:             if (document == null)
00047:                 return;
00048: 
00049:             PromptKeywordOptions options = new PromptKeywordOptions(
00050:                 "\nTypical Details [SetRoot/Search/Insert/Info] <Search>: ")
00051:             {
00052:                 AllowNone = true
00053:             };
00054:             options.Keywords.Add("SetRoot");
00055:             options.Keywords.Add("Search");
00056:             options.Keywords.Add("Insert");
00057:             options.Keywords.Add("Info");
00058: 
00059:             PromptResult result = document.Editor.GetKeywords(options);
00060:             if (result.Status == PromptStatus.Cancel)
00061:                 return;
00062: 
00063:             string choice = result.Status == PromptStatus.OK
00064:                 ? result.StringResult
00065:                 : "Search";
00066: 
00067:             if (choice.Equals("SetRoot", StringComparison.OrdinalIgnoreCase))
00068:                 SetLibraryRoot();
00069:             else if (choice.Equals("Insert", StringComparison.OrdinalIgnoreCase))
00070:                 InsertDetail();
00071:             else if (choice.Equals("Info", StringComparison.OrdinalIgnoreCase))
00072:                 ShowLibraryInformation();
00073:             else
00074:                 SearchLibrary();
00075:         }
00076: 
00077:         [CommandMethod("CE_DETAILSETROOT", CommandFlags.Modal)]
00078:         public void SetLibraryRoot()
00079:         {
00080:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00081:             if (document == null)
00082:                 return;
00083: 
00084:             string currentRoot = ReadLibraryRoot(document.Database);
00085:             var browser = new System.Windows.Forms.FolderBrowserDialog
00086:             {
00087:                 Description = "Select the master CE Tools Typical Details folder",
00088:                 ShowNewFolderButton = false,
00089:                 SelectedPath = Directory.Exists(currentRoot) ? currentRoot : string.Empty
00090:             };
00091:             if (browser.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
00092: 
00093:             string root;
00094:             try
00095:             {
00096:                 root = Path.GetFullPath(
00097:                     Environment.ExpandEnvironmentVariables(browser.SelectedPath));
00098:             }
00099:             catch (System.Exception exception)
00100:             {
00101:                 document.Editor.WriteMessage("\nCE_DETAILSETROOT: invalid folder path. " + exception.Message);
00102:                 return;
00103:             }
00104: 
00105:             if (!Directory.Exists(root))
00106:             {
00107:                 document.Editor.WriteMessage("\nCE_DETAILSETROOT: folder not found: " + root);
00108:                 return;
00109:             }
00110: 
00111:             WriteLibraryRoot(document.Database, root);
00112:             int count = EnumerateAssets(root).Count;
00113:             document.Editor.WriteMessage(
00114:                 "\nTypical Details master library saved. Supported assets found: " +
00115:                 count +
00116:                 ". Root: " +
00117:                 root);
00118:         }
00119: 
00120:         [CommandMethod("CE_DETAILSEARCH", CommandFlags.Modal)]
00121:         public void SearchLibrary()
00122:         {
00123:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00124:             if (document == null)
00125:                 return;
00126: 
00127:             Editor editor = document.Editor;
00128:             string root = RequireLibraryRoot(document);
00129:             if (string.IsNullOrWhiteSpace(root))
00130:                 return;
00131: 
00132:             PromptStringOptions options = new PromptStringOptions(
00133:                 "\nSearch Typical Details by name, category or keyword <all>: ")
00134:             {
00135:                 AllowSpaces = true
00136:             };
00137:             PromptResult result = editor.GetString(options);
00138:             if (result.Status == PromptStatus.Cancel)
00139:                 return;
00140: 
00141:             string query = result.Status == PromptStatus.OK
00142:                 ? result.StringResult.Trim()
00143:                 : string.Empty;
00144: 
00145:             List<DetailAsset> matches = FindAssets(root, query, null);
00146:             WriteSearchResults(editor, matches, root);
00147: 
00148:             editor.WriteMessage(
00149:                 "\nPhase 1 catalogue formats: DWG can be inserted; DXF and PDF are indexed for review/reference. " +
00150:                 "Only office-approved, engineer-reviewed details should be issued.");
00151:         }
00152: 
00153:         [CommandMethod("CE_DETAILINSERT", CommandFlags.Modal)]
00154:         public void InsertDetail()
00155:         {
00156:             Document document = AcApplication.DocumentManager.MdiActiveDocument;
00157:             if (document == null)
00158:                 return;
00159: 
00160:             Editor editor = document.Editor;
00161:             Database database = document.Database;
00162:             string root = RequireLibraryRoot(document);
00163:             if (string.IsNullOrWhiteSpace(root))
00164:                 return;
00165: 
00166:             PromptStringOptions searchOptions = new PromptStringOptions(
00167:                 "\nSearch approved DWG details by name, category or keyword <all>: ")
00168:             {
00169:                 AllowSpaces = true
00170:             };
00171:             PromptResult searchResult = editor.GetString(searchOptions);
00172:             if (searchResult.Status == PromptStatus.Cancel)
00173:                 return;
00174: 
00175:             string query = searchResult.Status == PromptStatus.OK
00176:                 ? searchResult.StringResult.Trim()
00177:                 : string.Empty;
00178: 
00179:             List<DetailAsset> matches = FindAssets(root, query, ".dwg");
00180:             WriteSearchResults(editor, matches, root);
00181:             if (matches.Count == 0)
00182:                 return;
00183: 
00184:             int displayedCount = Math.Min(matches.Count, MaximumDisplayedResults);
00185:             PromptIntegerOptions numberOptions = new PromptIntegerOptions(
00186:                 "\nEnter the DWG detail number to insert: ")
00187:             {
00188:                 AllowNegative = false,
00189:                 AllowZero = false,
00190:                 LowerLimit = 1,
00191:                 UpperLimit = displayedCount
00192:             };
00193:             PromptIntegerResult numberResult = editor.GetInteger(numberOptions);
00194:             if (numberResult.Status != PromptStatus.OK)
00195:                 return;
00196: 
00197:             DetailAsset selected = matches[numberResult.Value - 1];
00198: 
00199:             PromptPointResult pointResult = editor.GetPoint(
00200:                 "\nSpecify the typical-detail insertion point: ");
00201:             if (pointResult.Status != PromptStatus.OK)
00202:                 return;
00203: 
00204:             PromptDoubleOptions scaleOptions = new PromptDoubleOptions(
00205:                 "\nUniform detail scale <1.0>: ")
00206:             {
00207:                 AllowNegative = false,
00208:                 AllowZero = false,
00209:                 UseDefaultValue = true,
00210:                 DefaultValue = 1.0
00211:             };
00212:             PromptDoubleResult scaleResult = editor.GetDouble(scaleOptions);
00213:             if (scaleResult.Status != PromptStatus.OK)
00214:                 return;
00215: 
00216:             PromptDoubleOptions rotationOptions = new PromptDoubleOptions(
00217:                 "\nRotation in degrees <0>: ")
00218:             {
00219:                 AllowNegative = true,
00220:                 AllowZero = true,
00221:                 UseDefaultValue = true,
00222:                 DefaultValue = 0.0
00223:             };
00224:             PromptDoubleResult rotationResult = editor.GetDouble(rotationOptions);
00225:             if (rotationResult.Status != PromptStatus.OK)
```

## TypicalDetailsReviewCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 44-126
```csharp
00044:             "Stormwater",
00045:             "Sewer",
00046:             "Water",
00047:             "Earthworks",
00048:             "Parking",
00049:             "Landscaping",
00050:             "Structures",
00051:             "Standard Construction Notes",
00052:             "General Details"
00053:         };
00054: 
00055:         [CommandMethod("CE_DETAILREVIEWTOOLS", CommandFlags.Modal)]
00056:         public void ReviewTools()
00057:         {
00058:             Document document = ActiveDocument();
00059:             if (document == null)
00060:                 return;
00061: 
00062:             var options = new PromptKeywordOptions(
00063:                 "\nTypical-detail standards review [Single/Library/Report/Settings/Information] <Single>: ")
00064:             {
00065:                 AllowNone = true
00066:             };
00067:             foreach (string keyword in new[]
00068:             {
00069:                 "Single", "Library", "Report", "Settings", "Information"
00070:             })
00071:                 options.Keywords.Add(keyword);
00072:             PromptResult result = document.Editor.GetKeywords(options);
00073:             if (result.Status == PromptStatus.Cancel)
00074:                 return;
00075: 
00076:             string choice = result.Status == PromptStatus.OK
00077:                 ? result.StringResult
00078:                 : "Single";
00079:             if (choice.Equals("Library", StringComparison.OrdinalIgnoreCase))
00080:                 ReviewLibrary();
00081:             else if (choice.Equals("Report", StringComparison.OrdinalIgnoreCase))
00082:                 ShowStoredReport();
00083:             else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
00084:                 ConfigureSettings();
00085:             else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase))
00086:                 Information();
00087:             else
00088:                 ReviewSingle();
00089:         }
00090: 
00091:         [CommandMethod("CE_DETAILREVIEWSETTINGS", CommandFlags.Modal)]
00092:         public void ConfigureSettings()
00093:         {
00094:             Document document = ActiveDocument();
00095:             if (document == null)
00096:                 return;
00097: 
00098:             Editor editor = document.Editor;
00099:             ReviewSettings settings = ReviewSettings.Read(document.Database);
00100:             if (!PromptText(editor, "Approved text styles (comma separated; blank = review only)", settings.ApprovedTextStyles, out settings.ApprovedTextStyles))
00101:                 return;
00102:             if (!PromptText(editor, "Approved dimension styles (comma separated; blank = review only)", settings.ApprovedDimensionStyles, out settings.ApprovedDimensionStyles))
00103:                 return;
00104:             if (!PromptText(editor, "Preferred layer prefix (blank = no prefix rule)", settings.LayerPrefix, out settings.LayerPrefix))
00105:                 return;
00106:             if (!PromptText(editor, "Title/title-block keywords", settings.TitleKeywords, out settings.TitleKeywords))
00107:                 return;
00108:             if (!PromptText(editor, "Revision keywords", settings.RevisionKeywords, out settings.RevisionKeywords))
00109:                 return;
00110:             if (!PromptText(editor, "General-notes keywords", settings.NotesKeywords, out settings.NotesKeywords))
00111:                 return;
00112:             if (!PromptText(editor, "Legend keywords", settings.LegendKeywords, out settings.LegendKeywords))
00113:                 return;
00114:             if (!PromptText(editor, "North-arrow keywords", settings.NorthArrowKeywords, out settings.NorthArrowKeywords))
00115:                 return;
00116:             if (!PromptText(editor, "Company-logo keywords", settings.LogoKeywords, out settings.LogoKeywords))
00117:                 return;
00118:             if (!PromptText(editor, "Sheet-number attribute/text keywords", settings.SheetNumberKeywords, out settings.SheetNumberKeywords))
00119:                 return;
00120:             if (!PromptText(editor, "Scale keywords", settings.ScaleKeywords, out settings.ScaleKeywords))
00121:                 return;
00122:             if (!PromptPositiveInteger(editor, "Maximum library files per review run", settings.MaximumFiles, out settings.MaximumFiles))
00123:                 return;
00124:             if (!PromptPositiveInteger(editor, "Maximum findings per file", settings.MaximumFindingsPerFile, out settings.MaximumFindingsPerFile))
00125:                 return;
00126: 
```

### Lines 1362-1511
```csharp
01362:         {
01363:             return values == null || values.Count == 0
01364:                 ? "<None>"
01365:                 : string.Join(", ", values);
01366:         }
01367: 
01368:         private static string EmptyAsAny(string value)
01369:         {
01370:             return string.IsNullOrWhiteSpace(value) ? "<Not configured>" : value;
01371:         }
01372: 
01373:         private static string Encode(string value)
01374:         {
01375:             return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));
01376:         }
01377: 
01378:         private static string Decode(string value)
01379:         {
01380:             try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); }
01381:             catch { return string.Empty; }
01382:         }
01383: 
01384:         private static string[] SplitEncoded(string payload, int expected)
01385:         {
01386:             string[] raw = (payload ?? string.Empty).Split('|');
01387:             var values = new string[expected];
01388:             for (int index = 0; index < expected; index++)
01389:                 values[index] = index < raw.Length ? Decode(raw[index]) : string.Empty;
01390:             return values;
01391:         }
01392: 
01393:         private static object ReadProperty(object owner, string propertyName)
01394:         {
01395:             if (owner == null)
01396:                 return null;
01397:             PropertyInfo property = owner.GetType().GetProperty(
01398:                 propertyName,
01399:                 BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
01400:             if (property == null || !property.CanRead)
01401:                 return null;
01402:             try { return property.GetValue(owner, null); }
01403:             catch { return null; }
01404:         }
01405: 
01406:         private static bool PromptText(
01407:             Editor editor,
01408:             string label,
01409:             string current,
01410:             out string value)
01411:         {
01412:             var options = new PromptStringOptions(
01413:                 "\n" + label + " <" + (current ?? string.Empty) + ">: ")
01414:             {
01415:                 AllowSpaces = true
01416:             };
01417:             PromptResult result = editor.GetString(options);
01418:             if (result.Status == PromptStatus.Cancel)
01419:             {
01420:                 value = current;
01421:                 return false;
01422:             }
01423:             value = result.Status == PromptStatus.None
01424:                 ? current
01425:                 : result.StringResult.Trim();
01426:             return true;
01427:         }
01428: 
01429:         private static bool PromptPositiveInteger(
01430:             Editor editor,
01431:             string label,
01432:             int current,
01433:             out int value)
01434:         {
01435:             var options = new PromptIntegerOptions(
01436:                 "\n" + label + " <" + current.ToString(CultureInfo.InvariantCulture) + ">: ")
01437:             {
01438:                 AllowNegative = false,
01439:                 AllowZero = false,
01440:                 UseDefaultValue = true,
01441:                 DefaultValue = current
01442:             };
01443:             PromptIntegerResult result = editor.GetInteger(options);
01444:             value = result.Status == PromptStatus.OK ? result.Value : current;
01445:             return result.Status == PromptStatus.OK;
01446:         }
01447: 
01448:         private static bool Confirm(Editor editor, string message)
01449:         {
01450:             var options = new PromptKeywordOptions(
01451:                 "\n" + message + "? [Yes/No] <No>: ")
01452:             {
01453:                 AllowNone = true
01454:             };
01455:             options.Keywords.Add("Yes");
01456:             options.Keywords.Add("No");
01457:             PromptResult result = editor.GetKeywords(options);
01458:             return result.Status == PromptStatus.OK &&
01459:                    result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
01460:         }
01461: 
01462:         private static Document ActiveDocument()
01463:         {
01464:             return AcApplication.DocumentManager.MdiActiveDocument;
01465:         }
01466: 
01467:         private sealed class DetailReviewResult
01468:         {
01469:             public DetailReviewResult(string path, string format, string category, DateTime modifiedUtc)
01470:             {
01471:                 Path = path ?? string.Empty;
01472:                 Format = format ?? string.Empty;
01473:                 Category = category ?? string.Empty;
01474:                 ModifiedUtc = modifiedUtc;
01475:                 Findings = new List<ReviewFinding>();
01476:             }
01477:             public string Path { get; }
01478:             public string Format { get; }
01479:             public string Category { get; }
01480:             public DateTime ModifiedUtc { get; }
01481:             public List<ReviewFinding> Findings { get; }
01482:             public void Add(string severity, string area, string finding, string evidence)
01483:             {
01484:                 Findings.Add(new ReviewFinding(severity, area, finding, evidence));
01485:             }
01486:             public void Trim(int maximum)
01487:             {
01488:                 if (Findings.Count <= maximum)
01489:                     return;
01490:                 int removed = Findings.Count - maximum + 1;
01491:                 Findings.RemoveRange(maximum - 1, Findings.Count - (maximum - 1));
01492:                 Findings.Add(new ReviewFinding(
01493:                     "Review",
01494:                     "Report limit",
01495:                     "Additional findings were truncated",
01496:                     removed.ToString(CultureInfo.InvariantCulture) + " row(s) omitted; increase MaximumFindingsPerFile to review more."));
01497:             }
01498:         }
01499: 
01500:         private sealed class ReviewFinding
01501:         {
01502:             public ReviewFinding(string severity, string area, string finding, string evidence)
01503:             {
01504:                 Severity = severity ?? string.Empty;
01505:                 Area = area ?? string.Empty;
01506:                 Finding = finding ?? string.Empty;
01507:                 Evidence = evidence ?? string.Empty;
01508:             }
01509:             public string Severity { get; }
01510:             public string Area { get; }
01511:             public string Finding { get; }
```

## WaterProductionCommands.cs
Hits: `PromptStringOptions`, `GetString(`

### Lines 1562-1639
```csharp
01562:                     .Where(value => value != null)
01563:                     .ToArray();
01564:                 if (values.Length < 4)
01565:                     return false;
01566:                 objectType = values[0];
01567:                 routeName = values[1];
01568:                 sourceHandle = values[2];
01569:                 extra = values[3];
01570:                 return true;
01571:             }
01572:         }
01573: 
01574:         private static bool PromptText(
01575:             Editor editor,
01576:             string label,
01577:             string current,
01578:             out string value)
01579:         {
01580:             var options = new PromptStringOptions(
01581:                 "\n" + label + " <" + (current ?? string.Empty) + ">: ")
01582:             {
01583:                 AllowSpaces = true
01584:             };
01585:             PromptResult result = editor.GetString(options);
01586:             if (result.Status == PromptStatus.Cancel)
01587:             {
01588:                 value = current;
01589:                 return false;
01590:             }
01591:             value = result.Status == PromptStatus.None
01592:                 ? current
01593:                 : result.StringResult.Trim();
01594:             return true;
01595:         }
01596: 
01597:         private static bool PromptPositiveDouble(
01598:             Editor editor,
01599:             string label,
01600:             double current,
01601:             out double value)
01602:         {
01603:             var options = new PromptDoubleOptions(
01604:                 "\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
01605:             {
01606:                 AllowNegative = false,
01607:                 AllowZero = false,
01608:                 UseDefaultValue = true,
01609:                 DefaultValue = current
01610:             };
01611:             PromptDoubleResult result = editor.GetDouble(options);
01612:             value = result.Status == PromptStatus.OK ? result.Value : current;
01613:             return result.Status == PromptStatus.OK;
01614:         }
01615: 
01616:         private static bool Confirm(Editor editor, string message)
01617:         {
01618:             return DisciplineWorkflowDialogs.Confirm("CE Tools — Water", message + "?");
01619:         }
01620: 
01621:         private static string[] ReadCivilNames(
01622:             IEnumerable<ObjectId> ids,
01623:             Transaction transaction)
01624:         {
01625:             return ids.Select(id => transaction.GetObject(id, OpenMode.ForRead, false))
01626:                 .Select(item => Convert.ToString(ReadProperty(item, "Name"), CultureInfo.InvariantCulture))
01627:                 .Where(name => !string.IsNullOrWhiteSpace(name))
01628:                 .ToArray();
01629:         }
01630: 
01631:         private static IEnumerable<ObjectId> GetAlignmentProfileIds(
01632:             CivilDocument civilDocument,
01633:             Transaction transaction,
01634:             bool profileViews)
01635:         {
01636:             var result = new List<ObjectId>();
01637:             foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
01638:             {
01639:                 var alignment = transaction.GetObject(
```

## WaterSewerCostEstimateCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 144-224
```csharp
00144:                 "CE Tools - Water & Sewer Cost Estimate",
00145:                 "Quantities are linked to current drawing assets; workbook rates remain user-editable.",
00146:                 rows,
00147:                 "CE TOOLS WATER AND SEWER COST ESTIMATE");
00148:         }
00149: 
00150:         [CommandMethod("CE_TOOLS", "CE_WSCOSTAUTO", CommandFlags.Modal)]
00151:         public void ToggleAuto()
00152:         {
00153:             Document document = ActiveDocument();
00154:             if (document == null) return;
00155:             CostEstimateLink link = ReadLink(document.Database);
00156:             if (link == null)
00157:             {
00158:                 document.Editor.WriteMessage(
00159:                     "\nCE_WSCOSTAUTO stopped. Create a linked estimate first.");
00160:                 return;
00161:             }
00162:             var options = new PromptKeywordOptions(
00163:                 "\nAutomatic water/sewer estimate refresh [On/Off] <" +
00164:                 (link.Automatic ? "On" : "Off") + ">: ")
00165:             {
00166:                 AllowNone = true
00167:             };
00168:             options.Keywords.Add("On");
00169:             options.Keywords.Add("Off");
00170:             PromptResult result = document.Editor.GetKeywords(options);
00171:             if (result.Status == PromptStatus.Cancel) return;
00172:             bool enabled = result.Status == PromptStatus.None
00173:                 ? link.Automatic
00174:                 : Equal(result.StringResult, "On");
00175:             WriteLink(document.Database, new CostEstimateLink(
00176:                 link.Schema, link.Path, link.UnitsPerMetre, enabled));
00177:             document.Editor.WriteMessage(
00178:                 "\nAutomatic water/sewer cost-estimate refresh is {0}.",
00179:                 enabled ? "ON" : "OFF");
00180:         }
00181: 
00182:         internal static int RefreshAll(Document document)
00183:         {
00184:             return RefreshAll(document, false);
00185:         }
00186: 
00187:         internal static int RefreshAll(Document document, bool report)
00188:         {
00189:             if (document == null) return 0;
00190:             CostEstimateLink link = ReadLink(document.Database);
00191:             if (link == null || string.IsNullOrWhiteSpace(link.Path)) return 0;
00192:             if (!File.Exists(link.Path))
00193:             {
00194:                 if (report)
00195:                     document.Editor.WriteMessage(
00196:                         "\nLinked cost-estimate workbook was not found: {0}",
00197:                         link.Path);
00198:                 return 0;
00199:             }
00200:             try
00201:             {
00202:                 CostEstimateSnapshot snapshot = CostEstimateCollector.Read(
00203:                     document.Database,
00204:                     link.UnitsPerMetre);
00205:                 WaterSewerWorkbookUpdater.Update(link.Path, snapshot);
00206:                 if (report)
00207:                     document.Editor.WriteMessage(
00208:                         "\nCE_WSCOSTREFRESH complete. Water length={0:N2} m; sewer length={1:N2} m; workbook={2}",
00209:                         snapshot.WaterLength,
00210:                         snapshot.SewerLength,
00211:                         link.Path);
00212:                 return 1;
00213:             }
00214:             catch (System.Exception exception)
00215:             {
00216:                 if (report)
00217:                     document.Editor.WriteMessage(
00218:                         "\nCE_WSCOSTREFRESH failed; workbook was left recoverable. {0}",
00219:                         exception.Message);
00220:                 return 0;
00221:             }
00222:         }
00223: 
00224:         internal static bool IsAutomatic(Database database)
```

## WorkflowRepairCommands.cs
Hits: `PromptStringOptions`, `PromptKeywordOptions`, `GetString(`, `GetKeywords(`

### Lines 294-373
```csharp
00294:             List<SurfaceChoice> surfaces = ReadSurfaceChoices(document);
00295:             if (surfaces.Count == 0)
00296:             {
00297:                 editor.WriteMessage(
00298:                     "\nCE_FLSURFACEUI cancelled. The current Civil 3D drawing contains no accessible surfaces.");
00299:                 return;
00300:             }
00301: 
00302:             var dialog = new SurfaceSelectionWindow(surfaces);
00303:             AcApplication.ShowModalWindow(dialog);
00304:             SurfaceChoice selectedSurface = dialog.SelectedSurface;
00305:             if (selectedSurface == null)
00306:             {
00307:                 editor.WriteMessage(
00308:                     "\nCE_FLSURFACEUI cancelled. No surface was selected.");
00309:                 return;
00310:             }
00311: 
00312:             var gradeBreakOptions = new PromptKeywordOptions(
00313:                 "\nInsert intermediate surface grade-break points? [Yes/No] <No>: ")
00314:             {
00315:                 AllowNone = true
00316:             };
00317:             gradeBreakOptions.Keywords.Add("Yes");
00318:             gradeBreakOptions.Keywords.Add("No");
00319:             PromptResult gradeBreakResult = editor.GetKeywords(gradeBreakOptions);
00320:             if (gradeBreakResult.Status == PromptStatus.Cancel) return;
00321:             bool includeIntermediate = gradeBreakResult.Status == PromptStatus.OK &&
00322:                 string.Equals(
00323:                     gradeBreakResult.StringResult,
00324:                     "Yes",
00325:                     StringComparison.OrdinalIgnoreCase);
00326: 
00327:             FeatureLineMutationPreview preview = BuildFeatureLinePreview(
00328:                 document.Database,
00329:                 selection);
00330:             if (preview.EditableIds.Count == 0)
00331:             {
00332:                 WriteRejectedSummary(editor, preview.RejectedReasons);
00333:                 editor.WriteMessage(
00334:                     "\nCE_FLSURFACEUI cancelled. No editable ordinary feature lines were selected.");
00335:                 return;
00336:             }
00337: 
00338:             var reviewRows = new List<KeyValuePair<string, string>>
00339:             {
00340:                 new KeyValuePair<string, string>("Surface", selectedSurface.Name),
00341:                 new KeyValuePair<string, string>("Surface type", selectedSurface.Type),
00342:                 new KeyValuePair<string, string>("Surface style", selectedSurface.Style),
00343:                 new KeyValuePair<string, string>(
00344:                     "Feature lines",
00345:                     preview.EditableIds.Count.ToString(CultureInfo.InvariantCulture)),
00346:                 new KeyValuePair<string, string>(
00347:                     "Intermediate grade breaks",
00348:                     includeIntermediate ? "Yes" : "No"),
00349:                 new KeyValuePair<string, string>(
00350:                     "Rejected",
00351:                     preview.RejectedCount.ToString(CultureInfo.InvariantCulture))
00352:             };
00353:             AppendRejectedRows(reviewRows, preview.RejectedReasons);
00354: 
00355:             if (!PopupTablePresenter.ShowReview(
00356:                 "CE Tools Feature Line Surface Assignment",
00357:                 "Review the selected Civil 3D surface before changing feature-line elevations.",
00358:                 reviewRows,
00359:                 "Assign"))
00360:             {
00361:                 editor.WriteMessage(
00362:                     "\nCE_FLSURFACEUI cancelled. No feature-line elevations were changed.");
00363:                 return;
00364:             }
00365: 
00366:             int changed = 0;
00367:             int skipped = 0;
00368:             try
00369:             {
00370:                 using (Transaction transaction =
00371:                     document.Database.TransactionManager.StartTransaction())
00372:                 {
00373:                     foreach (ObjectId objectId in preview.EditableIds)
```

### Lines 611-690
```csharp
00611:             Document document = ActiveDocument();
00612:             if (document == null) return;
00613: 
00614:             Editor editor = document.Editor;
00615:             PromptSelectionResult selection = GetSelection(
00616:                 editor,
00617:                 "\nSelect parking bay blocks and/or closed bay polylines to validate and number: ");
00618:             if (selection.Status != PromptStatus.OK) return;
00619: 
00620:             AnnotationOptions annotationOptions;
00621:             if (!AnnotationSettingsStore.Prepare(
00622:                 document,
00623:                 false,
00624:                 out annotationOptions))
00625:             {
00626:                 return;
00627:             }
00628: 
00629:             var prefixOptions = new PromptStringOptions(
00630:                 "\nEnter parking number prefix <P>: ")
00631:             {
00632:                 AllowSpaces = false,
00633:                 DefaultValue = "P",
00634:                 UseDefaultValue = true
00635:             };
00636:             PromptResult prefixResult = editor.GetString(prefixOptions);
00637:             if (prefixResult.Status != PromptStatus.OK) return;
00638: 
00639:             var startOptions = new PromptIntegerOptions(
00640:                 "\nEnter starting parking number <1>: ")
00641:             {
00642:                 AllowNone = true,
00643:                 DefaultValue = 1,
00644:                 UseDefaultValue = true
00645:             };
00646:             PromptIntegerResult startResult = editor.GetInteger(startOptions);
00647:             if (startResult.Status != PromptStatus.OK) return;
00648: 
00649:             var incrementOptions = new PromptIntegerOptions(
00650:                 "\nEnter numbering increment <1>: ")
00651:             {
00652:                 AllowNone = true,
00653:                 DefaultValue = 1,
00654:                 UseDefaultValue = true
00655:             };
00656:             PromptIntegerResult incrementResult = editor.GetInteger(incrementOptions);
00657:             if (incrementResult.Status != PromptStatus.OK) return;
00658:             if (incrementResult.Value == 0)
00659:             {
00660:                 editor.WriteMessage(
00661:                     "\nCE_PKNUMBER2 cancelled. The numbering increment cannot be zero.");
00662:                 return;
00663:             }
00664: 
00665:             ParkingValidationResult validation = BuildParkingValidation(
00666:                 document.Database,
00667:                 selection);
00668:             if (validation.Candidates.Count == 0)
00669:             {
00670:                 WriteRejectedSummary(editor, validation.RejectedReasons);
00671:                 editor.WriteMessage(
00672:                     "\nCE_PKNUMBER2 cancelled. No valid parking bay blocks or closed polylines were selected.");
00673:                 return;
00674:             }
00675: 
00676:             var rows = new List<KeyValuePair<string, string>>
00677:             {
00678:                 new KeyValuePair<string, string>(
00679:                     "Accepted parking bays",
00680:                     validation.Candidates.Count.ToString(CultureInfo.InvariantCulture)),
00681:                 new KeyValuePair<string, string>(
00682:                     "Rejected objects",
00683:                     validation.RejectedCount.ToString(CultureInfo.InvariantCulture)),
00684:                 new KeyValuePair<string, string>("Prefix", prefixResult.StringResult),
00685:                 new KeyValuePair<string, string>(
00686:                     "Starting number",
00687:                     startResult.Value.ToString(CultureInfo.InvariantCulture)),
00688:                 new KeyValuePair<string, string>(
00689:                     "Increment",
00690:                     incrementResult.Value.ToString(CultureInfo.InvariantCulture)),
```

## XrefProjectManagementCommands.cs
Hits: `PromptKeywordOptions`, `GetKeywords(`

### Lines 58-137
```csharp
00058:             Editor editor = document.Editor;
00059: 
00060:             var saveOptions = new PromptSaveFileOptions(
00061:                 "\nChoose a base path for discipline XREF drawings: ")
00062:             {
00063:                 DialogCaption = "CE Tools Project XREF Discipline Split",
00064:                 Filter = "AutoCAD Drawing (*.dwg)|*.dwg",
00065:                 InitialFileName = DefaultProjectPrefix(document.Database) + "-XREF.dwg"
00066:             };
00067:             PromptFileNameResult fileResult = editor.GetFileNameForSave(saveOptions);
00068:             if (fileResult.Status != PromptStatus.OK) return;
00069:             string selectedPath = EnsureDwgExtension(fileResult.StringResult);
00070:             string folder = Path.GetDirectoryName(selectedPath) ?? string.Empty;
00071:             string prefix = Path.GetFileNameWithoutExtension(selectedPath);
00072:             if (prefix.EndsWith("-XREF", StringComparison.OrdinalIgnoreCase))
00073:                 prefix = prefix.Substring(0, prefix.Length - "-XREF".Length);
00074:             if (string.IsNullOrWhiteSpace(prefix)) prefix = "CE-PROJECT";
00075: 
00076:             var replaceOptions = new PromptKeywordOptions(
00077:                 "\nAfter successful XREF attachment [Keep/Replace] original model-space objects <Keep>: ")
00078:             {
00079:                 AllowNone = true
00080:             };
00081:             replaceOptions.Keywords.Add("Keep");
00082:             replaceOptions.Keywords.Add("Replace");
00083:             PromptResult replaceResult = editor.GetKeywords(replaceOptions);
00084:             if (replaceResult.Status == PromptStatus.Cancel) return;
00085:             bool replace = replaceResult.Status == PromptStatus.OK &&
00086:                 Equal(replaceResult.StringResult, "Replace");
00087: 
00088:             DisciplineSplitPlan plan = BuildSplitPlan(
00089:                 document.Database,
00090:                 folder,
00091:                 prefix);
00092:             if (plan.Groups.Count == 0)
00093:             {
00094:                 editor.WriteMessage(
00095:                     "\nCE_XREFDISCIPLINESPLIT stopped. No editable non-XREF model-space objects were available.");
00096:                 return;
00097:             }
00098:             if (plan.ExistingPaths.Count > 0)
00099:             {
00100:                 editor.WriteMessage(
00101:                     "\nCE_XREFDISCIPLINESPLIT stopped. Existing discipline files will not be overwritten:");
00102:                 foreach (string path in plan.ExistingPaths.Take(12))
00103:                     editor.WriteMessage("\n  {0}", path);
00104:                 return;
00105:             }
00106: 
00107:             var review = new List<KeyValuePair<string, string>>
00108:             {
00109:                 Pair("Output folder", folder),
00110:                 Pair("File prefix", prefix),
00111:                 Pair("Discipline drawings", plan.Groups.Count.ToString(CultureInfo.InvariantCulture)),
00112:                 Pair("Objects to export", plan.TotalObjects.ToString(CultureInfo.InvariantCulture)),
00113:                 Pair("Locked/dependent/XREF objects skipped", plan.SkippedObjects.ToString(CultureInfo.InvariantCulture)),
00114:                 Pair("Original model-space objects", replace ? "Replace only after every file and attachment succeeds" : "Keep"),
00115:                 Pair("Overwrite existing files", "Never")
00116:             };
00117:             foreach (DisciplineSplitGroup group in plan.Groups)
00118:             {
00119:                 review.Add(Pair(
00120:                     group.Discipline,
00121:                     group.ObjectIds.Count + " objects → " + group.Path));
00122:             }
00123:             if (!PopupTablePresenter.ShowReview(
00124:                     "CE Tools - Project XREF Discipline Split",
00125:                     "Objects are grouped from controlled layer-name keywords. Review every group before confirming. Existing DWGs are never overwritten.",
00126:                     review,
00127:                     "Create Discipline XREFs"))
00128:             {
00129:                 editor.WriteMessage("\nCE_XREFDISCIPLINESPLIT cancelled.");
00130:                 return;
00131:             }
00132: 
00133:             var createdFiles = new List<string>();
00134:             try
00135:             {
00136:                 Directory.CreateDirectory(folder);
00137:                 foreach (DisciplineSplitGroup group in plan.Groups)
```
