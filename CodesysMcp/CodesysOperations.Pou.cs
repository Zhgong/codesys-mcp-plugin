using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using _3S.CoDeSys.ScriptEngine;
using _3S.CoDeSys.ScriptEngine.BasicFunctionality;

namespace CodesysMcp
{
    public static partial class CodesysOperations
    {
        public static PouContentResult ReadPou(string name, bool includeLiveEditorOverlay = true)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var extObj = FindObjectByName(name);
                if (extObj == null)
                {
                    throw new FileNotFoundException("POU/Object not found: " + name);
                }

                var obj = GetExtension<IScriptObject>(extObj) ?? extObj as IScriptObject;
                if (obj == null) throw new InvalidOperationException("Could not resolve IScriptObject for POU: " + name);
                var marker = GetExtension<IScriptTextualObjectMarker>(extObj);
                bool hasDecl = marker != null && marker.has_textual_declaration;
                bool hasImpl = marker != null && marker.has_textual_implementation;
                string declText = string.Empty;
                string implText = string.Empty;

                if (hasDecl)
                {
                    var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extObj);
                    var doc = declExt?.textual_declaration;
                    if (doc != null) declText = doc.text ?? string.Empty;
                }

                if (hasImpl)
                {
                    var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extObj);
                    var doc = implExt?.textual_implementation;
                    if (doc != null) implText = doc.text ?? string.Empty;
                }

                if (includeLiveEditorOverlay)
                {
                    var liveBuffers = CodesysEditorBufferHelper.CollectLiveEditorSnapshots();
                    CodesysEditorBufferHelper.TryApplyLiveSnapshot(
                        liveBuffers,
                        obj.guid,
                        obj.get_name(),
                        hasDecl,
                        hasImpl,
                        ref declText,
                        ref implText);
                }

                return new PouContentResult
                {
                    name = obj.get_name(),
                    guid = obj.guid.ToString(),
                    type = obj.type.ToString(),
                    declaration = declText,
                    implementation = implText,
                    hasDeclaration = hasDecl,
                    hasImplementation = hasImpl
                };
            });
        }

        public static PouContentResult WritePou(string name, string declaration, string implementation, string pouTypeStr = "Program")
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var extObj = FindObjectByName(name);

                // Capture baseline snapshot before first modification in session
                if (!_snapshots.ContainsKey(name))
                {
                    if (extObj != null)
                    {
                        var current = ReadPou(name);
                        _snapshots[name] = new PouSnapshot
                        {
                            Name = name,
                            PouType = current.type,
                            OriginalDeclaration = current.declaration,
                            OriginalImplementation = current.implementation,
                            ExistedBefore = true,
                            IsDeleted = false,
                            CreatedAt = DateTime.UtcNow
                        };
                    }
                    else
                    {
                        _snapshots[name] = new PouSnapshot
                        {
                            Name = name,
                            PouType = pouTypeStr,
                            OriginalDeclaration = string.Empty,
                            OriginalImplementation = string.Empty,
                            ExistedBefore = false,
                            IsDeleted = false,
                            CreatedAt = DateTime.UtcNow
                        };
                    }
                }

                if (extObj == null)
                {
                    var projExt = CodesysContext.PrimaryProjectExtended;
                    if (projExt == null) throw new InvalidOperationException("No active project open.");
                    var appExt = GetActiveApplicationObject(projExt.BaseObject);
                    object targetContainerObj = appExt ?? projExt.BaseObject;
                    string leafName = name;
                    if (name.Contains(".") || name.Contains("/"))
                    {
                        char sep = name.Contains("/") ? '/' : '.';
                        int lastSep = name.LastIndexOf(sep);
                        string parentPath = name.Substring(0, lastSep);
                        leafName = name.Substring(lastSep + 1);
                        if (string.Equals(pouTypeStr, "Method", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(pouTypeStr, "Property", StringComparison.OrdinalIgnoreCase))
                        {
                            targetContainerObj = FindObjectByName(parentPath);
                            if (targetContainerObj == null)
                            {
                                throw new InvalidOperationException($"Parent POU/Interface '{parentPath}' not found for {pouTypeStr} '{leafName}'.");
                            }
                        }
                        else
                        {
                            targetContainerObj = EnsureFolderPath(parentPath);
                        }
                    }
                    if (string.Equals(pouTypeStr, "Folder", StringComparison.OrdinalIgnoreCase))
                    {
                        var folderCreator = targetContainerObj as IScriptObject3 ?? GetExtension<IScriptObject3>(targetContainerObj);
                        if (folderCreator == null) throw new InvalidOperationException($"Cannot create folder under '{targetContainerObj}'.");
                        folderCreator.create_folder(leafName);
                        var tree = targetContainerObj as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(targetContainerObj);
                        var matches = tree?.find(leafName, false);
                        extObj = (matches != null && matches.Count > 0) ? matches[0] : null;
                    }
                    else if (string.Equals(pouTypeStr, "Method", StringComparison.OrdinalIgnoreCase))
                    {
                        var memberContainer = targetContainerObj as IScriptIecLanguageMemberContainer
                                           ?? GetExtension<IScriptIecLanguageMemberContainer>(targetContainerObj);
                        if (memberContainer == null) throw new InvalidOperationException($"Parent '{targetContainerObj}' does not support creating methods.");
                        string returnType = ExtractReturnTypeFromDeclaration(declaration);
                        extObj = memberContainer.create_method(leafName, returnType, null);
                    }
                    else if (string.Equals(pouTypeStr, "Property", StringComparison.OrdinalIgnoreCase))
                    {
                        var memberContainer = targetContainerObj as IScriptIecLanguageMemberContainer
                                           ?? GetExtension<IScriptIecLanguageMemberContainer>(targetContainerObj);
                        if (memberContainer == null) throw new InvalidOperationException($"Parent '{targetContainerObj}' does not support creating properties.");
                        string returnType = ExtractReturnTypeFromDeclaration(declaration);
                        extObj = memberContainer.create_property(leafName, returnType, null);
                    }
                    else
                    {
                        var container = targetContainerObj as IScriptIecLanguageObjectContainer
                                     ?? GetExtension<IScriptIecLanguageObjectContainer>(targetContainerObj)
                                     ?? GetExtension<IScriptIecLanguageObjectContainer>(appExt)
                                     ?? GetExtension<IScriptIecLanguageObjectContainer>(projExt);
                        if (container == null)
                        {
                            throw new InvalidOperationException("Could not resolve IScriptIecLanguageObjectContainer on target container, active application or project.");
                        }
                        if (string.Equals(pouTypeStr, "Interface", StringComparison.OrdinalIgnoreCase))
                        {
                            extObj = container.create_interface(leafName, null);
                        }
                        else if (string.Equals(pouTypeStr, "DUT", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(pouTypeStr, "Structure", StringComparison.OrdinalIgnoreCase))
                        {
                            extObj = container.create_dut(leafName, DutType.Structure, null);
                        }
                        else if (string.Equals(pouTypeStr, "Enumeration", StringComparison.OrdinalIgnoreCase))
                        {
                            extObj = container.create_dut(leafName, DutType.Enumeration, null);
                        }
                        else if (string.Equals(pouTypeStr, "GVL", StringComparison.OrdinalIgnoreCase))
                        {
                            extObj = container.create_gvl(leafName);
                        }
                        else
                        {
                            PouType pouType = PouType.Program;
                            string returnType = null;
                            if (string.Equals(pouTypeStr, "FunctionBlock", StringComparison.OrdinalIgnoreCase))
                            {
                                pouType = PouType.FunctionBlock;
                            }
                            else if (string.Equals(pouTypeStr, "Function", StringComparison.OrdinalIgnoreCase))
                            {
                                pouType = PouType.Function;
                                returnType = ExtractReturnTypeFromDeclaration(declaration) ?? "BOOL";
                            }
                            extObj = container.create_pou(leafName, pouType, null, returnType, null, null);
                        }
                    }
                }

                if (extObj == null) throw new Exception("Failed to find or create POU: " + name);

                if (declaration != null)
                {
                    var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extObj);
                    declExt?.textual_declaration?.replace(declaration);
                }

                if (implementation != null)
                {
                    var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extObj);
                    implExt?.textual_implementation?.replace(implementation);
                }

                var written = ReadPou(name, includeLiveEditorOverlay: false);
                TryMirrorWrittenObject(extObj, written);
                return written;
            });
        }

        public static object EnsureFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var parts = path.Split(new[] { '/', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var projExt = CodesysContext.PrimaryProjectExtended;
            if (projExt == null) throw new InvalidOperationException("No active project open.");
            var current = GetActiveApplicationObject(projExt.BaseObject) ?? projExt.BaseObject;
            foreach (var part in parts)
            {
                var tree = current as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(current);
                object next = null;
                if (tree != null)
                {
                    var matches = tree.find(part, false);
                    if (matches != null && matches.Count > 0)
                    {
                        next = matches[0];
                    }
                }
                if (next == null)
                {
                    var folderCreator = current as IScriptObject3 ?? GetExtension<IScriptObject3>(current);
                    if (folderCreator == null)
                    {
                        throw new InvalidOperationException($"Cannot create folder '{part}' under object '{current}'.");
                    }
                    folderCreator.create_folder(part);
                    tree = current as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(current);
                    var matches = tree?.find(part, false);
                    next = (matches != null && matches.Count > 0) ? matches[0] : null;
                }
                current = next;
            }
            return current;
        }

        private static string ExtractReturnTypeFromDeclaration(string declaration)
        {
            if (string.IsNullOrWhiteSpace(declaration)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                declaration,
                @"(?:METHOD|FUNCTION|PROPERTY)\s+(?:PRIVATE\s+|PROTECTED\s+|INTERNAL\s+|PUBLIC\s+)?(?:\w+)\s*:\s*([A-Za-z0-9_()]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
            return match.Success ? match.Groups[1].Value : null;
        }

        public static object CreateFolder(string name, string parentPath = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var targetParent = string.IsNullOrEmpty(parentPath)
                    ? (GetActiveApplicationObject(CodesysContext.PrimaryProject) ?? CodesysContext.PrimaryProject)
                    : EnsureFolderPath(parentPath);
                var folderCreator = targetParent as IScriptObject3 ?? GetExtension<IScriptObject3>(targetParent);
                if (folderCreator == null)
                    throw new InvalidOperationException($"Cannot create folder under '{targetParent}'.");
                folderCreator.create_folder(name);
                var tree = targetParent as IScriptTreeObject ?? GetExtension<IScriptTreeObject>(targetParent);
                var matches = tree?.find(name, false);
                return (matches != null && matches.Count > 0) ? matches[0] : null;
            });
        }

        public static object CreateInterface(string name, string parentPath = null, string baseInterfaces = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var targetParent = string.IsNullOrEmpty(parentPath)
                    ? (GetActiveApplicationObject(CodesysContext.PrimaryProject) ?? CodesysContext.PrimaryProject)
                    : EnsureFolderPath(parentPath);
                var container = targetParent as IScriptIecLanguageObjectContainer
                             ?? GetExtension<IScriptIecLanguageObjectContainer>(targetParent);
                if (container == null)
                    throw new InvalidOperationException($"Cannot resolve IScriptIecLanguageObjectContainer under '{targetParent}'.");
                var created = container.create_interface(name, baseInterfaces);
                TryMirrorWrittenObject(created, null);
                return created;
            });
        }

        public static PouContentResult CreateMethod(string parentPou, string name, string returnType = null, string declaration = null, string implementation = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var parent = FindObjectByName(parentPou);
                if (parent == null) throw new InvalidOperationException($"Parent POU/Interface '{parentPou}' not found.");
                var memberContainer = parent as IScriptIecLanguageMemberContainer
                                   ?? GetExtension<IScriptIecLanguageMemberContainer>(parent);
                if (memberContainer == null) throw new InvalidOperationException($"Parent '{parentPou}' does not support creating methods.");
                var extObj = memberContainer.create_method(name, returnType, null);
                if (declaration != null)
                {
                    var declExt = GetExtension<IScriptObjectWithTextualDeclaration>(extObj);
                    declExt?.textual_declaration?.replace(declaration);
                }
                if (implementation != null)
                {
                    var implExt = GetExtension<IScriptObjectWithTextualImplementation>(extObj);
                    implExt?.textual_implementation?.replace(implementation);
                }
                var createdMethod = ReadPou($"{parentPou}.{name}");
                TryMirrorWrittenObject(extObj, createdMethod);
                return createdMethod;
            });
        }

        public static object CreateProperty(string parentPou, string name, string returnType = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var parent = FindObjectByName(parentPou);
                if (parent == null) throw new InvalidOperationException($"Parent POU/Interface '{parentPou}' not found.");
                var memberContainer = parent as IScriptIecLanguageMemberContainer
                                   ?? GetExtension<IScriptIecLanguageMemberContainer>(parent);
                if (memberContainer == null) throw new InvalidOperationException($"Parent '{parentPou}' does not support creating properties.");
                var createdProp = memberContainer.create_property(name, returnType, null);
                TryMirrorWrittenObject(createdProp, null);
                return createdProp;
            });
        }

        public static bool DeletePou(string name)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var extObj = FindObjectByName(name);
                if (extObj == null) return false;

                // Capture baseline snapshot before deletion
                if (!_snapshots.ContainsKey(name))
                {
                    var current = ReadPou(name);
                    _snapshots[name] = new PouSnapshot
                    {
                        Name = name,
                        PouType = current.type,
                        OriginalDeclaration = current.declaration,
                        OriginalImplementation = current.implementation,
                        ExistedBefore = true,
                        IsDeleted = true,
                        CreatedAt = DateTime.UtcNow
                    };
                }

                string relPath = SourceMirrorHelper.HasActiveMirror ? GetObjectRelativePath(extObj) : null;
                var obj = GetExtension<IScriptObject>(extObj) ?? extObj as IScriptObject;
                obj?.remove();
                if (!string.IsNullOrWhiteSpace(relPath) && SourceMirrorHelper.HasActiveMirror)
                {
                    try
                    {
                        if (SourceMirrorHelper.DeleteSingleNode(SourceMirrorHelper.ActiveMirrorDirectory, relPath))
                        {
                            SourceMirrorHelper.RemoveIdeContentHash(relPath);
                            CodesysContext.LogToIde($"[Mirror → Disk] Deleted: {relPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        CodesysContext.LogToIde($"[Mirror → Disk] Delete failed for '{relPath}': {ex.Message}");
                    }
                }
                return true;
            });
        }
    }
}
