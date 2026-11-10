using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidworksExecution.Models;


namespace SolidworksExecution.Services
{
    // SolidWorksService partial: manage_configurations — list, add, activate, rename or delete the
    // configurations of the active part or assembly. Variants of one assembly (e.g. a left and a right
    // hand harness) are configurations whose components are suppressed or resolved per configuration
    // (set_component_state acts on the active configuration) and whose sub-assemblies reference a
    // named configuration (set_component_state(referenced_configuration)).
    public partial class SolidWorksService
    {
        public ExecutionResponse ManageConfigurations(ToolRequest request)
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
                string action = (p?.Value<string>("action") ?? "list").Trim().ToLowerInvariant();
                string name = p?.Value<string>("name") ?? "";
                string newName = p?.Value<string>("new_name") ?? "";
                string description = p?.Value<string>("description") ?? "";
                bool activate = p?.Value<bool?>("activate") ?? true;
                bool suppressNew = p?.Value<bool?>("suppress_new_components") ?? false;

                var doc = _solidWorks.IActiveDoc2 as IModelDoc2;
                if (doc == null)
                    return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                        "NO_ACTIVE_DOCUMENT", "No active document found in SolidWorks.");

                bool changed = false;
                var result = new JObject { ["document"] = doc.GetTitle(), ["action"] = action };
                switch (action)
                {
                    case "list":
                        break;
                    case "add":
                    {
                        if (string.IsNullOrEmpty(name))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "MISSING_PARAMETER", "name is required to add a configuration.");
                        if (doc.GetConfigurationByName(name) != null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "ALREADY_EXISTS", $"Configuration '{name}' already exists.");
                        // The new configuration copies the active one. swConfigOption_DontActivate = 128.
                        int opts = activate ? 0 : 128;
                        var cfg = doc.AddConfiguration3(name, description, "", opts) as IConfiguration;
                        if (cfg == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "ADD_FAILED", $"AddConfiguration3 failed for '{name}'.");
                        if (!string.IsNullOrEmpty(description)) { try { cfg.Description = description; } catch { } }
                        try { cfg.SuppressNewComponentModels = suppressNew; } catch { }
                        changed = true;
                        break;
                    }
                    case "activate":
                        // ShowConfiguration2 returns false for the configuration that is already active.
                        if (string.Equals((doc.GetActiveConfiguration() as IConfiguration)?.Name, name, StringComparison.Ordinal))
                            break;
                        if (string.IsNullOrEmpty(name) || !doc.ShowConfiguration2(name))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "ACTIVATE_FAILED", $"Could not activate configuration '{name}'.");
                        doc.EditRebuild3();
                        changed = true;
                        break;
                    case "rename":
                    {
                        var cfg = doc.GetConfigurationByName(name) as IConfiguration;
                        if (cfg == null || string.IsNullOrEmpty(newName))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "RENAME_FAILED", "rename needs an existing name and new_name.");
                        cfg.Name = newName;
                        changed = true;
                        break;
                    }
                    case "delete":
                        if (string.IsNullOrEmpty(name) || !doc.DeleteConfiguration2(name))
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "DELETE_FAILED", $"Could not delete '{name}' (the active configuration cannot be deleted).");
                        changed = true;
                        break;
                    case "set":
                    {
                        // Configuration properties: description and suppress_new_components.
                        var cfg = doc.GetConfigurationByName(string.IsNullOrEmpty(name)
                            ? (doc.GetActiveConfiguration() as IConfiguration)?.Name : name) as IConfiguration;
                        if (cfg == null)
                            return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                                "NOT_FOUND", $"No configuration '{name}'.");
                        if (p?["description"] != null) cfg.Description = description;
                        if (p?["suppress_new_components"] != null) cfg.SuppressNewComponentModels = suppressNew;
                        changed = true;
                        break;
                    }
                    default:
                        return BuildFailed(request.OperationId, _guard.GetCurrentStateVersion(),
                            "INVALID_PARAMETER", "action must be list, add, activate, rename, delete or set.");
                }

                var active = doc.GetActiveConfiguration() as IConfiguration;
                var list = new JArray();
                if (doc.GetConfigurationNames() is object[] names)
                    foreach (var o in names)
                    {
                        var n = o as string;
                        var cfg = doc.GetConfigurationByName(n) as IConfiguration;
                        var cj = new JObject { ["name"] = n };
                        if (active != null && n == active.Name) cj["active"] = true;
                        try { if (!string.IsNullOrEmpty(cfg?.Description) && cfg.Description != n) cj["description"] = cfg.Description; } catch { }
                        try { if (cfg != null && cfg.SuppressNewComponentModels) cj["suppress_new_components"] = true; } catch { }
                        try { var par = cfg?.GetParent() as IConfiguration; if (par != null) cj["parent"] = par.Name; } catch { }
                        list.Add(cj);
                    }
                result["active"] = active?.Name;
                result["configurations"] = list;
                if (changed) result["note"] = "Not saved.";

                int sv = _guard.GetCurrentStateVersion() + (changed ? 1 : 0);
                var response = new ExecutionResponse
                {
                    OperationId = request.OperationId,
                    Status = "COMPLETED",
                    Verified = true,
                    StateVersion = sv,
                    CadState = new CadState
                    {
                        StateVersion = sv,
                        ActiveDocument = doc.GetTitle(),
                        DocumentType = doc is IAssemblyDoc ? "ASSEMBLY" : "PART",
                        ActiveSketch = null,
                        Features = new List<string> { result.ToString(Newtonsoft.Json.Formatting.None) },
                        Dimensions = new List<string>()
                    },
                    Error = null
                };
                if (changed) _guard.RegisterCompleted(request.OperationId, response);
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
