using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: screenshot — saves the active document's graphics view as a PNG
    // (IModelDoc2.SaveBMP, an image capture that never writes the document file), optionally after
    // switching to a standard view orientation and zooming to fit, with an optional caption band
    // drawn under the image. Changes only the on-screen view; never bumps state_version.
    public partial class SolidWorksService
    {
        // swStandardViews_e, inlined (swconst enums must not be used as runtime types).
        private static readonly Dictionary<string, int> StandardViews = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "front", 1 }, { "back", 2 }, { "left", 3 }, { "right", 4 }, { "top", 5 }, { "bottom", 6 },
            { "isometric", 7 }, { "trimetric", 8 }, { "dimetric", 9 },
        };

        public ExecutionResponse Screenshot(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            string tmpBmp = null;
            try
            {
                var p = request.Params as JObject;
                string filePath = p?.Value<string>("file_path") ?? "";
                string view = p?.Value<string>("view") ?? "current";
                bool fit = p?.Value<bool?>("zoom_to_fit") ?? true;
                int width = p?.Value<int?>("width") ?? 0;
                int height = p?.Value<int?>("height") ?? 0;
                string caption = p?.Value<string>("caption") ?? "";

                if (string.IsNullOrWhiteSpace(filePath) ||
                    !filePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "INVALID_PARAMETER", "file_path must be a full path ending in .png.");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (modelDoc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                if (!string.Equals(view, "current", StringComparison.OrdinalIgnoreCase))
                {
                    if (!StandardViews.TryGetValue(view, out int viewId))
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER",
                            $"view '{view}' is not supported. Supported: current, {string.Join(", ", StandardViews.Keys)}.");
                    modelDoc.ShowNamedView2("", viewId);
                }
                // focus: zoom to these components (instance names or paths; world boxes), optionally
                // with everything else hidden for the capture (isolate) and shown again afterwards.
                // zoom_box: [x1,y1,z1,x2,y2,z2] in meters, model (assembly) coordinates.
                var focus = ParseNameList(p?["focus"]);
                bool isolate = p?.Value<bool?>("isolate") ?? false;
                double margin = p?.Value<double?>("margin") ?? 0.1;
                double[] zb = null;
                var zbTok = p?["zoom_box"];
                if (zbTok != null && zbTok.Type != JTokenType.Null)
                {
                    var za = zbTok.Type == JTokenType.String ? JArray.Parse((string)zbTok) : zbTok as JArray;
                    zb = za?.Select(t => (double)t).ToArray();
                    if (zb != null && zb.Length != 6)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "zoom_box needs 6 numbers [x1,y1,z1,x2,y2,z2] in meters.");
                }
                var info = new JObject();
                bool focusZoomed = false;
                if (focus.Count > 0)
                {
                    var asmF = modelDoc as IAssemblyDoc;
                    if (asmF == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "WRONG_DOCUMENT_TYPE", "focus needs an active assembly.");
                    var comps = new List<IComponent2>();
                    foreach (var n in focus)
                    {
                        var c = FindComponentByName(asmF, n);
                        if (c == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "COMPONENT_NOT_FOUND", ComponentNotFoundMessage(asmF, n));
                        comps.Add(c);
                    }
                    double[] u = null;
                    foreach (var c in comps)
                    {
                        var b = ComponentBox(c);
                        if (b == null) continue;
                        if (u == null) u = (double[])b.Clone();
                        else for (int i = 0; i < 3; i++) { u[i] = Math.Min(u[i], b[i]); u[i + 3] = Math.Max(u[i + 3], b[i + 3]); }
                    }
                    if (u == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "NO_GEOMETRY", "The focus components have no bounding box (suppressed or lightweight without graphics).");
                    if (zb == null)
                    {
                        // SolidWorks' own zoom to the selected components.
                        modelDoc.ViewZoomtofit2();
                        modelDoc.ClearSelection2(true);
                        foreach (var c in comps) try { c.Select4(true, null, false); } catch { }
                        modelDoc.ViewZoomToSelection();
                        modelDoc.ClearSelection2(true);
                        focusZoomed = true;
                    }
                    info["focus_box_mm"] = new JArray(u.Select(v => Math.Round(v * 1000, 1)));
                    if (isolate && !IsolateComponents(asmF, comps)) info["isolate_failed"] = true;
                }
                if (focusZoomed) { }
                else if (zb != null)
                {
                    modelDoc.ViewZoomtofit2();
                    ZoomToModelBox(modelDoc, zb, margin);
                }
                else if (fit) modelDoc.ViewZoomtofit2();
                modelDoc.GraphicsRedraw2();

                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                tmpBmp = Path.Combine(Path.GetTempPath(), "solidpilot_" + Guid.NewGuid().ToString("N") + ".bmp");

                if (!modelDoc.SaveBMP(tmpBmp, width, height) || !File.Exists(tmpBmp))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "SCREENSHOT_FAILED", "IModelDoc2.SaveBMP did not write an image (is the document window visible?).");

                int outW, outH;
                using (var shot = new Bitmap(tmpBmp))
                using (var composed = ComposeWithCaption(shot, caption))
                {
                    composed.Save(filePath, ImageFormat.Png);
                    outW = composed.Width;
                    outH = composed.Height;
                }

                info["file_path"] = filePath;
                info["width"] = outW;
                info["height"] = outH;
                info["view"] = view;
                if (_isolated != null)
                {
                    info["isolated"] = true;
                    RestoreHidden(modelDoc);
                }

                return new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion(),
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion(),
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = null,
                        ActiveSketch = null,
                        Features = new List<string> { info.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "UNEXPECTED_ERROR", ex.Message);
            }
            finally
            {
                if (tmpBmp != null) try { File.Delete(tmpBmp); } catch { }
                if (_isolated != null) try { RestoreHidden(_solidWorks.IActiveDoc2 as IModelDoc2); } catch { }
            }
        }

        private IAssemblyDoc _isolated;

        // SolidWorks' own Isolate command on the selected components (IAssemblyDoc.Isolate, a
        // temporary view mode left with ExitIsolate). Setting IComponent2.Visible per component did
        // not show in the capture (and took 83 s for ~850 leaves).
        private bool IsolateComponents(IAssemblyDoc asm, List<IComponent2> comps)
        {
            var doc = asm as IModelDoc2;
            doc.ClearSelection2(true);
            foreach (var c in comps) try { c.Select4(true, null, false); } catch { }
            bool ok = false;
            try { asm.SetIsolateVisibility(0 /* hidden */); } catch { }
            try { ok = asm.Isolate(); } catch { }
            doc.ClearSelection2(true);
            if (ok) _isolated = asm;
            return ok;
        }

        private void RestoreHidden(IModelDoc2 doc)
        {
            var asm = _isolated;
            _isolated = null;
            if (asm == null) return;
            try { asm.ExitIsolate(); } catch { }
            try { (asm as IModelDoc2)?.GraphicsRedraw2(); } catch { }
        }

        // ViewZoomTo2 takes the zoom rectangle in VIEW coordinates (the screen-aligned frame of
        // IModelView.Orientation3), not the corners of a model-space box: passing a box's min / max
        // corners framed another region in an isometric view (found 2026-11-13 by comparing with
        // ViewZoomToSelection on the same component; only view coordinates matched). Project the 8
        // corners into the view frame (row vectors, v = p * R), take their 2D extent plus the margin.
        private static void ZoomToModelBox(IModelDoc2 doc, double[] b, double margin)
        {
            var view = doc.ActiveView as IModelView;
            var ori = view?.Orientation3 as MathTransform;
            var a = ori?.ArrayData as double[];
            if (a == null || a.Length < 9)
            {
                doc.ViewZoomTo2(b[0], b[1], b[2], b[3], b[4], b[5]);
                return;
            }
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, sumZ = 0;
            for (int m = 0; m < 8; m++)
            {
                double x = (m & 1) == 0 ? b[0] : b[3], y = (m & 2) == 0 ? b[1] : b[4], z = (m & 4) == 0 ? b[2] : b[5];
                double vx = x * a[0] + y * a[3] + z * a[6];
                double vy = x * a[1] + y * a[4] + z * a[7];
                sumZ += x * a[2] + y * a[5] + z * a[8];
                minX = Math.Min(minX, vx); maxX = Math.Max(maxX, vx);
                minY = Math.Min(minY, vy); maxY = Math.Max(maxY, vy);
            }
            double zc = sumZ / 8, mx = (maxX - minX) * margin, my = (maxY - minY) * margin;
            doc.ViewZoomTo2(minX - mx, minY - my, zc, maxX + mx, maxY + my, zc);
        }

        // The screenshot, with the caption's lines (split on '\n') in a white band below it.
        private static Bitmap ComposeWithCaption(Bitmap shot, string caption)
        {
            if (string.IsNullOrWhiteSpace(caption))
                return new Bitmap(shot);

            string[] lines = caption.Replace("\r\n", "\n").Split('\n');
            using (var font = new Font("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bold = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                const int pad = 12, gap = 4;
                int bandH = pad * 2;
                using (var probe = Graphics.FromImage(shot))
                {
                    for (int i = 0; i < lines.Length; i++)
                        bandH += (int)Math.Ceiling(probe.MeasureString(lines[i], i == 0 ? bold : font).Height) + gap;
                }

                var outBmp = new Bitmap(shot.Width, shot.Height + bandH);
                using (var g = Graphics.FromImage(outBmp))
                {
                    g.Clear(Color.White);
                    g.DrawImage(shot, 0, 0, shot.Width, shot.Height);
                    g.DrawLine(Pens.Gray, 0, shot.Height, shot.Width, shot.Height);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    float y = shot.Height + pad;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var f = i == 0 ? bold : font;
                        g.DrawString(lines[i], f, Brushes.Black, pad, y);
                        y += g.MeasureString(lines[i], f).Height + gap;
                    }
                }
                return outBmp;
            }
        }
    }
}
