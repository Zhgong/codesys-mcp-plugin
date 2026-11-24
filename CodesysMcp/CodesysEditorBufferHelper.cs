using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;

namespace CodesysMcp
{
    public class LiveEditorSnapshot
    {
        public Guid ObjectGuid { get; set; }
        public string ObjectName { get; set; }
        public string Declaration { get; set; }
        public string Implementation { get; set; }
    }

    /// <summary>
    /// Reflection-only reader for active CODESYS IDE ST/POU editor buffers (IPOUEditor / ITextDocumentProvider).
    /// Reads live unsaved text without stealing focus, moving the cursor, or mutating UI controls.
    /// </summary>
    public static class CodesysEditorBufferHelper
    {
        private static readonly HashSet<string> _loggedAttachedEditors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static Dictionary<string, LiveEditorSnapshot> CollectLiveEditorSnapshots()
        {
            var result = new Dictionary<string, LiveEditorSnapshot>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var forms = Application.OpenForms;
                if (forms == null || forms.Count == 0) return result;

                var snapshotForms = new List<Form>();
                foreach (Form f in forms)
                {
                    if (f != null && !f.IsDisposed) snapshotForms.Add(f);
                }

                foreach (var form in snapshotForms)
                {
                    ScanControlTree(form, null, result, 0);
                }
            }
            catch { }

            return result;
        }

        private static void ScanControlTree(Control control, string containerTitle, Dictionary<string, LiveEditorSnapshot> result, int depth)
        {
            if (control == null || control.IsDisposed || depth > 28) return;

            string title = containerTitle;
            try
            {
                string ctrlText = control.Text;
                if (!string.IsNullOrWhiteSpace(ctrlText) &&
                    ctrlText.Length <= 128 &&
                    !ctrlText.Contains("\n") &&
                    (control is Form || control.GetType().Name.IndexOf("Dock", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     control.GetType().Name.IndexOf("Tab", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     control.GetType().Name.IndexOf("Page", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    title = CleanEditorTitle(ctrlText);
                }
            }
            catch { }

            if (TryExtractPouEditorSnapshot(control, title, out var snap))
            {
                RegisterSnapshot(result, snap);
                LogAttachedEditorOnce(snap);
                return; // IPOUEditor already covers both InterfaceEditorView and ImplementationEditorView
            }

            try
            {
                if (!control.HasChildren) return;
                foreach (Control child in control.Controls)
                {
                    ScanControlTree(child, title, result, depth + 1);
                }
            }
            catch { }
        }

        public static bool TryExtractPouEditorSnapshot(object editorHost, string fallbackTitle, out LiveEditorSnapshot snapshot)
        {
            snapshot = null;
            if (editorHost == null) return false;

            Type t = editorHost.GetType();
            bool isPouEditor = t.GetInterface("IPOUEditor") != null;
            PropertyInfo ifaceProp = t.GetProperty("InterfaceEditorView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            PropertyInfo implProp = t.GetProperty("ImplementationEditorView", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (!isPouEditor && ifaceProp == null && implProp == null)
            {
                return false;
            }

            object ifaceView = SafeGetProp(editorHost, ifaceProp);
            object implView = SafeGetProp(editorHost, implProp);

            string declText = ExtractTextFromView(ifaceView, 0);
            string implText = ExtractTextFromView(implView, 0);
            if (string.IsNullOrWhiteSpace(declText)) declText = null;
            if (string.IsNullOrWhiteSpace(implText)) implText = null;

            if (declText == null && implText == null)
            {
                return false;
            }

            Guid objGuid = TryResolveObjectGuid(editorHost, ifaceView, implView);
            string objName = TryResolveObjectName(editorHost, ifaceView, implView) ?? CleanEditorTitle(fallbackTitle);

            if (objGuid == Guid.Empty && string.IsNullOrWhiteSpace(objName))
            {
                return false;
            }

            snapshot = new LiveEditorSnapshot
            {
                ObjectGuid = objGuid,
                ObjectName = objName,
                Declaration = declText,
                Implementation = implText
            };
            return true;
        }

        private static void RegisterSnapshot(Dictionary<string, LiveEditorSnapshot> map, LiveEditorSnapshot snap)
        {
            if (snap == null) return;
            if (snap.ObjectGuid != Guid.Empty)
            {
                map["guid:" + snap.ObjectGuid.ToString("N")] = snap;
            }
            if (!string.IsNullOrWhiteSpace(snap.ObjectName))
            {
                string key = "name:" + snap.ObjectName.Trim();
                if (!map.ContainsKey(key))
                {
                    map[key] = snap;
                }
            }
        }

        /// <summary>
        /// Applies non-whitespace live IDE editor text onto declaration/implementation buffers by GUID or object name.
        /// Never overwrites existing text when the live buffer section is null or whitespace-only.
        /// </summary>
        public static bool TryApplyLiveSnapshot(
            Dictionary<string, LiveEditorSnapshot> liveBuffers,
            Guid objectGuid,
            string objectName,
            bool hasDecl,
            bool hasImpl,
            ref string declaration,
            ref string implementation)
        {
            if (liveBuffers == null || liveBuffers.Count == 0) return false;

            LiveEditorSnapshot live = null;
            if (objectGuid != Guid.Empty)
            {
                liveBuffers.TryGetValue("guid:" + objectGuid.ToString("N"), out live);
            }

            if (live == null && !string.IsNullOrWhiteSpace(objectName))
            {
                string trimmed = objectName.Trim();
                if (!liveBuffers.TryGetValue("name:" + trimmed, out live))
                {
                    int lastSep = trimmed.LastIndexOfAny(new[] { '/', '.' });
                    if (lastSep >= 0 && lastSep + 1 < trimmed.Length)
                    {
                        string leaf = trimmed.Substring(lastSep + 1).Trim();
                        if (!string.IsNullOrEmpty(leaf))
                        {
                            liveBuffers.TryGetValue("name:" + leaf, out live);
                        }
                    }
                }
            }

            if (live == null) return false;

            bool applied = false;
            if (hasDecl && !string.IsNullOrWhiteSpace(live.Declaration))
            {
                declaration = live.Declaration;
                applied = true;
            }

            if (hasImpl && !string.IsNullOrWhiteSpace(live.Implementation))
            {
                implementation = live.Implementation;
                applied = true;
            }

            return applied;
        }

        private static void LogAttachedEditorOnce(LiveEditorSnapshot snap)
        {
            if (snap == null) return;
            try
            {
                string key = snap.ObjectGuid != Guid.Empty
                    ? snap.ObjectGuid.ToString("N")
                    : (snap.ObjectName ?? string.Empty);
                if (string.IsNullOrEmpty(key)) return;

                lock (_loggedAttachedEditors)
                {
                    if (!_loggedAttachedEditors.Add(key)) return;
                }

                int declLen = snap.Declaration != null ? snap.Declaration.Length : 0;
                int implLen = snap.Implementation != null ? snap.Implementation.Length : 0;
                string label = !string.IsNullOrWhiteSpace(snap.ObjectName) ? snap.ObjectName : key;
                CodesysContext.LogToIde($"[Mirror] Live editor buffer attached: {label} (declLen={declLen}, implLen={implLen})");
            }
            catch { }
        }

        private static string ExtractTextFromView(object view, int depth)
        {
            if (view == null || depth > 12) return null;

            string typeName = view.GetType().Name;
            if (IsOverlayOrAuxiliaryControl(typeName))
            {
                return null;
            }

            // 1. Direct CODESYS SynEdControl.Buffer reader (fastest and immune to child overlay controls)
            string bufText = TryReadSynEdBuffer(view);
            if (bufText != null) return bufText;

            // 2. Document.Text / Document.GetText() reader (custom or mocked editors)
            string docText = TryReadDocumentProperty(view);
            if (docText != null) return docText;

            // 3. SynEdControl / SyntaxEditor leaf control Text BEFORE recursing into Controls
            if (IsSynEdControlTypeName(typeName))
            {
                try
                {
                    if (view is Control synCtrl && !synCtrl.IsDisposed)
                    {
                        return synCtrl.Text;
                    }
                    if (SafeGetPropByName(view, "Text") is string propText)
                    {
                        return propText;
                    }
                }
                catch { }
                return null; // Never recurse into SynEdControl child controls (e.g. IncrementalSearchControl)
            }

            // 4. Unwrap IECTextEditor -> SynEdControl
            object synEd = SafeGetPropByName(view, "SynEdControl") ?? SafeGetFieldByName(view, "_synEdControl");
            if (synEd != null && !ReferenceEquals(synEd, view))
            {
                string synText = ExtractTextFromView(synEd, depth + 1);
                if (synText != null) return synText;
            }

            // 5. Support SwitchableDeclarationEditor._text when in Textual mode
            if (typeName.IndexOf("SwitchableDeclarationEditor", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                object mode = SafeGetPropByName(view, "Mode");
                string modeStr = mode != null ? mode.ToString() : null;
                if (modeStr == null ||
                    string.Equals(modeStr, "Textual", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(modeStr, "0", StringComparison.Ordinal))
                {
                    object textEditorField = SafeGetFieldByName(view, "_text");
                    if (textEditorField != null && !ReferenceEquals(textEditorField, view))
                    {
                        string swText = ExtractTextFromView(textEditorField, depth + 1);
                        if (swText != null) return swText;
                    }
                }
            }

            // 6. ITextDocumentProvider / GetTextDocument() / GetTextDocument(false)
            string direct = TryReadTextDocumentProvider(view);
            if (direct != null) return direct;

            // 7. Container traversal (skipping overlay/auxiliary controls)
            if (view is Control ctrl && !ctrl.IsDisposed)
            {
                try
                {
                    foreach (Control child in ctrl.Controls)
                    {
                        if (child == null || child.IsDisposed) continue;
                        if (IsOverlayOrAuxiliaryControl(child.GetType().Name)) continue;

                        string found = ExtractTextFromView(child, depth + 1);
                        if (found != null) return found;
                    }
                }
                catch { }
            }

            return null;
        }

        private static bool IsOverlayOrAuxiliaryControl(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            return typeName.IndexOf("IncrementalSearch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("SmartTag", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("InfoBar", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("ToolStrip", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSynEdControlTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            return typeName.IndexOf("SynEd", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("SyntaxEditor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string TryReadSynEdBuffer(object candidate)
        {
            if (candidate == null) return null;
            try
            {
                object buffer = SafeGetPropByName(candidate, "Buffer") ?? SafeGetFieldByName(candidate, "_buffer");
                if (buffer == null) return null;

                object lenObj = SafeGetPropByName(buffer, "Length");
                if (!(lenObj is int len)) return null;
                if (len <= 0) return string.Empty;

                MethodInfo getText = buffer.GetType().GetMethod(
                    "GetText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(int), typeof(int) },
                    null);
                if (getText == null) return null;

                return getText.Invoke(buffer, new object[] { 0, len }) as string;
            }
            catch
            {
                return null;
            }
        }

        private static string TryReadDocumentProperty(object candidate)
        {
            if (candidate == null) return null;
            try
            {
                object doc = SafeGetPropByName(candidate, "Document");
                if (doc == null) return null;

                PropertyInfo textProp = doc.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (textProp != null && textProp.GetValue(doc, null) is string s)
                {
                    return s;
                }

                MethodInfo getTextZero = doc.GetType().GetMethod("GetText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getTextZero != null && getTextZero.Invoke(doc, null) is string gs)
                {
                    return gs;
                }
            }
            catch { }
            return null;
        }

        private static string TryReadTextDocumentProvider(object candidate)
        {
            if (candidate == null) return null;
            try
            {
                Type t = candidate.GetType();
                object doc = null;

                MethodInfo getDoc = t.GetMethod("GetTextDocument", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getDoc == null)
                {
                    var iface = t.GetInterface("ITextDocumentProvider");
                    if (iface != null)
                    {
                        getDoc = iface.GetMethod("GetTextDocument", Type.EmptyTypes);
                    }
                }

                if (getDoc != null)
                {
                    doc = getDoc.Invoke(candidate, null);
                }
                else
                {
                    MethodInfo getDocBool = t.GetMethod("GetTextDocument", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                    if (getDocBool != null)
                    {
                        doc = getDocBool.Invoke(candidate, new object[] { false });
                    }
                }

                if (doc == null) return null;

                PropertyInfo textProp = doc.GetType().GetProperty("Text", BindingFlags.Public | BindingFlags.Instance)
                    ?? doc.GetType().GetInterface("ITextDocument")?.GetProperty("Text");
                if (textProp == null) return null;

                return textProp.GetValue(doc, null) as string;
            }
            catch
            {
                return null;
            }
        }

        private static Guid TryResolveObjectGuid(params object[] hosts)
        {
            string[] guidProps = { "ObjectGuid", "Guid", "guid" };
            string[] ownerProps = { "EditedObject", "OwnerObject", "Object", "BaseObject", "ScriptObject", "Tag" };

            foreach (object h in hosts)
            {
                if (h == null) continue;
                foreach (string gp in guidProps)
                {
                    object val = SafeGetPropByName(h, gp);
                    if (val is Guid g && g != Guid.Empty) return g;
                }
                foreach (string op in ownerProps)
                {
                    object owner = SafeGetPropByName(h, op);
                    if (owner == null) continue;
                    foreach (string gp in guidProps)
                    {
                        object val = SafeGetPropByName(owner, gp);
                        if (val is Guid g && g != Guid.Empty) return g;
                    }
                }
            }
            return Guid.Empty;
        }

        private static string TryResolveObjectName(params object[] hosts)
        {
            string[] nameProps = { "ObjectName", "Name", "name" };
            string[] ownerProps = { "EditedObject", "OwnerObject", "Object", "BaseObject", "ScriptObject", "Tag" };

            foreach (object h in hosts)
            {
                if (h == null) continue;
                foreach (string op in ownerProps)
                {
                    object owner = SafeGetPropByName(h, op);
                    if (owner == null) continue;
                    foreach (string np in nameProps)
                    {
                        if (SafeGetPropByName(owner, np) is string s && !string.IsNullOrWhiteSpace(s))
                            return s.Trim();
                    }
                    try
                    {
                        MethodInfo getName = owner.GetType().GetMethod("get_name", Type.EmptyTypes);
                        if (getName != null && getName.Invoke(owner, null) is string gn && !string.IsNullOrWhiteSpace(gn))
                            return gn.Trim();
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string CleanEditorTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.EndsWith("*", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1).Trim();
            int paren = s.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) s = s.Substring(0, paren).Trim();
            return s;
        }

        private static object SafeGetProp(object obj, PropertyInfo prop)
        {
            if (obj == null || prop == null) return null;
            try { return prop.GetValue(obj, null); } catch { return null; }
        }

        private static object SafeGetPropByName(object obj, string propName)
        {
            if (obj == null || string.IsNullOrEmpty(propName)) return null;
            try
            {
                var p = obj.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return p != null ? p.GetValue(obj, null) : null;
            }
            catch { return null; }
        }

        private static object SafeGetFieldByName(object obj, string fieldName)
        {
            if (obj == null || string.IsNullOrEmpty(fieldName)) return null;
            try
            {
                Type cur = obj.GetType();
                while (cur != null)
                {
                    var f = cur.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (f != null) return f.GetValue(obj);
                    cur = cur.BaseType;
                }
                return null;
            }
            catch { return null; }
        }
    }
}
