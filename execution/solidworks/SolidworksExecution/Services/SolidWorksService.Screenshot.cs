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
                // zoom_box: [x1,y1,z1,x2,y2,z2] in meters, model (assembly) coordinates: zoom on that
                // region instead of fitting the whole model (IModelDoc2.ViewZoomTo2).
                double[] zb = (p?["zoom_box"] as JArray)?.Select(t => (double)t).ToArray();
                if (zb != null && zb.Length == 6)
                {
                    modelDoc.ViewZoomtofit2();
                    modelDoc.ViewZoomTo2(zb[0], zb[1], zb[2], zb[3], zb[4], zb[5]);
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

                var info = new JObject
                {
                    ["file_path"] = filePath,
                    ["width"] = outW,
                    ["height"] = outH,
                    ["view"] = view,
                };

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
            }
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
