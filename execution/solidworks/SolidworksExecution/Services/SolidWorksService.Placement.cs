using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: place_component — set an EXISTING component's transform and / or fixed
    // state in the active assembly (insert_component does the same for a component it inserts). Used to
    // fix parts modelled in the assembly's own coordinates at the identity transform, or to reposition a
    // component without deleting and re-inserting it (which would break the mates and references on it).
    public partial class SolidWorksService
    {
        public ExecutionResponse PlaceComponent(ToolRequest request)
        {
            if (_guard.IsDuplicate(request.OperationId))
                return _guard.GetDuplicate(request.OperationId);

            if (!_guard.IsStateVersionValid(request.StateVersion))
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "INVALID_STATE_VERSION", "Incoming state_version does not match current state.");

            if (!EnsureConnected())
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                    "COM_ATTACH_FAILED", "SolidWorks process not found or COM not registered.");

            try
            {
                var p = request.Params as JObject;
                string name = p?.Value<string>("component") ?? "";
                string transformJson = p?.Value<string>("transform_json") ?? "";
                bool? fixedFlag = p?["fixed"] == null || p["fixed"].Type == JTokenType.Null
                    ? (bool?)null : p.Value<bool>("fixed");

                var modelDoc = _solidWorks.IActiveDoc2 as IModelDoc2;
                var asm = modelDoc as IAssemblyDoc;
                if (asm == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "WRONG_DOCUMENT_TYPE", "place_component requires an active assembly.");
                if (string.IsNullOrEmpty(name))
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "MISSING_PARAMETER", "component (Name2 from analyze_assembly) is required.");

                IComponent2 comp = null;
                var names = new List<string>();
                if (asm.GetComponents(false) is object[] comps)
                    foreach (var o in comps)
                        if (o is IComponent2 c)
                        {
                            names.Add(c.Name2);
                            if (comp == null && string.Equals(c.Name2, name, StringComparison.OrdinalIgnoreCase)) comp = c;
                        }
                if (comp == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "COMPONENT_NOT_FOUND", $"No component '{name}'. Available: {string.Join(", ", names)}");

                if (!string.IsNullOrEmpty(transformJson) && transformJson != "[]")
                {
                    var tvals = JArray.Parse(transformJson);
                    if (tvals.Count < 13)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "transform_json needs 13 numbers: 9 rotation (row-major) + 3 translation + scale.");
                    var arr = new double[16];
                    for (int i = 0; i < 13; i++) arr[i] = (double)tvals[i];
                    var xf = _solidWorks.IGetMathUtility().CreateTransform(arr) as MathTransform;
                    if (xf == null)
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "TRANSFORM_FAILED", "CreateTransform rejected the array.");
                    // A fixed component keeps its position: unfix to move it, re-fix below if asked.
                    if (comp.IsFixed())
                    {
                        modelDoc.ClearSelection2(true);
                        if (comp.Select4(false, null, false)) asm.UnfixComponent();
                        modelDoc.ClearSelection2(true);
                        if (!fixedFlag.HasValue) fixedFlag = true;
                    }
                    comp.Transform2 = xf;
                }

                if (fixedFlag.HasValue && comp.IsFixed() != fixedFlag.Value)
                {
                    modelDoc.ClearSelection2(true);
                    if (comp.Select4(false, null, false))
                    {
                        if (fixedFlag.Value) asm.FixComponent(); else asm.UnfixComponent();
                    }
                    modelDoc.ClearSelection2(true);
                }
                modelDoc.EditRebuild3();

                var info = new JObject { ["component"] = comp.Name2, ["fixed"] = comp.IsFixed() };
                var tj = TransformJson(comp);
                if (tj != null) info["transform"] = tj;

                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = _guard.GetCurrentStateVersion() + 1,
                    CadState = new CadState
                    {
                        StateVersion = _guard.GetCurrentStateVersion() + 1,
                        ActiveDocument = modelDoc.GetTitle(),
                        DocumentType = "ASSEMBLY",
                        ActiveSketch = null,
                        Features = new List<string> { info.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
                _guard.RegisterCompleted(request.OperationId, response);
                return response;
            }
            catch (COMException ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "COM_ERROR", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(), "UNEXPECTED_ERROR", ex.Message);
            }
        }
    }
}
