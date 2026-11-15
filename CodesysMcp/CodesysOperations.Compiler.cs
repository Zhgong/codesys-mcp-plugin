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
        public static CompileResult CompileProject(bool clean = false, bool rebuild = false)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) throw new InvalidOperationException("No active project open.");

                var appExt = GetActiveApplicationObject(proj);
                var app = GetExtension<IScriptApplication>(appExt);
                if (app == null) throw new InvalidOperationException("No active application found in project.");

                if (clean)
                {
                    app.clean();
                }

                if (rebuild)
                {
                    app.rebuild();
                }
                else
                {
                    app.build();
                }

                return CollectCompilerMessages();
            }, 120000);
        }

        public static CompileResult CollectCompilerMessages(string category = "Build", string minSeverity = "Warning")
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var sys3 = CodesysContext.System as ISystem3;
                var res = new CompileResult();

                if (sys3 != null)
                {
                    var categoriesToQuery = new List<string>();
                    if (string.IsNullOrEmpty(category) || string.Equals(category, "Build", StringComparison.OrdinalIgnoreCase))
                    {
                        categoriesToQuery.Add("Build");
                    }
                    else if (string.Equals(category, "all", StringComparison.OrdinalIgnoreCase))
                    {
                        categoriesToQuery.Add("Build");
                        categoriesToQuery.Add("Precompile");
                        categoriesToQuery.Add("Script Messages");
                        categoriesToQuery.Add("Application Composer");
                    }
                    else
                    {
                        categoriesToQuery.Add(category);
                    }

                    _3S.CoDeSys.Core.Messages.Severity sevMask = (_3S.CoDeSys.Core.Messages.Severity)31;

                    foreach (var catName in categoriesToQuery)
                    {
                        System.Collections.Generic.IList<IScriptMessage> messages = null;
                        try
                        {
                            messages = sys3.get_message_objects(catName, sevMask);
                        }
                        catch
                        {
                            try
                            {
                                messages = sys3.get_message_objects();
                            }
                            catch { }
                        }

                        if (messages != null)
                        {
                            foreach (var msg in messages)
                            {
                                var scriptMsg = msg as IScriptMessage;
                                if (scriptMsg == null) continue;

                                string severity = scriptMsg.severity.ToString();
                                string text = scriptMsg.text ?? string.Empty;
                                int number = (int)(scriptMsg.number ?? 0);
                                string prefix = scriptMsg.prefix ?? string.Empty;
                                string posText = scriptMsg.position_text ?? string.Empty;

                                bool isError = severity.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               severity.IndexOf("Fatal", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool isWarning = severity.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool isInfo = severity.IndexOf("Information", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              severity.IndexOf("Info", StringComparison.OrdinalIgnoreCase) >= 0;

                                if (isError)
                                {
                                    res.errorCount++;
                                }
                                else if (isWarning)
                                {
                                    res.warningCount++;
                                }

                                if (string.Equals(minSeverity, "Error", StringComparison.OrdinalIgnoreCase) && !isError)
                                {
                                    continue;
                                }
                                if (string.Equals(minSeverity, "Warning", StringComparison.OrdinalIgnoreCase) && !isError && !isWarning)
                                {
                                    continue;
                                }
                                if (string.Equals(minSeverity, "Information", StringComparison.OrdinalIgnoreCase) && !isError && !isWarning && !isInfo)
                                {
                                    continue;
                                }

                                var targetObj = scriptMsg.@object;
                                string objName = null;
                                if (targetObj != null)
                                {
                                    var extObj = targetObj as IExtendedObject<IScriptObject>;
                                    if (extObj != null) objName = extObj.BaseObject.get_name();
                                    else if (targetObj is IScriptObject so) objName = so.get_name();
                                }

                                res.messages.Add(new CompilerMessageResult
                                {
                                    category = catName,
                                    text = text,
                                    severity = severity,
                                    number = number,
                                    prefix = prefix,
                                    objectName = objName,
                                    positionText = posText
                                });
                            }
                        }
                    }
                }

                res.success = res.errorCount == 0;
                return res;
            });
        }

        public static MessageCategoriesResult GetMessageCategories()
        {
            var result = new MessageCategoriesResult();
            var sys3 = CodesysContext.System as ISystem3;
            if (sys3 == null)
            {
                result.categories.Add(new MessageCategoryInfo { name = "Build", guid = "", totalMessages = 0, errors = 0, warnings = 0 });
                result.categories.Add(new MessageCategoryInfo { name = "Precompile", guid = "", totalMessages = 0, errors = 0, warnings = 0 });
                result.categories.Add(new MessageCategoryInfo { name = "Script Messages", guid = "{194B48A9-AB51-43ae-B9A9-51D3EDAADDF3}", totalMessages = 0, errors = 0, warnings = 0 });
                result.categories.Add(new MessageCategoryInfo { name = "Application Composer", guid = "", totalMessages = 0, errors = 0, warnings = 0 });
                return result;
            }

            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var catGuids = sys3.get_message_categories(false);
                    if (catGuids != null)
                    {
                        foreach (var g in catGuids)
                        {
                            string desc = null;
                            try { desc = sys3.get_message_category_description(g); } catch { }
                            string name = !string.IsNullOrEmpty(desc) ? desc : g.ToString();
                            knownNames.Add(name);

                            int total = 0, errs = 0, warns = 0;
                            try
                            {
                                var msgs = sys3.get_message_objects(g, (_3S.CoDeSys.Core.Messages.Severity)31);
                                if (msgs != null)
                                {
                                    total = msgs.Count;
                                    foreach (var m in msgs)
                                    {
                                        string sev = m.severity.ToString();
                                        if (sev.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || sev.IndexOf("Fatal", StringComparison.OrdinalIgnoreCase) >= 0) errs++;
                                        else if (sev.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) warns++;
                                    }
                                }
                            }
                            catch { }

                            result.categories.Add(new MessageCategoryInfo
                            {
                                name = name,
                                guid = g.ToString(),
                                totalMessages = total,
                                errors = errs,
                                warnings = warns
                            });
                        }
                    }
                }
                catch { }

                var standardCats = new[] { "Build", "Precompile", "Script Messages" };
                foreach (var std in standardCats)
                {
                    if (!knownNames.Contains(std))
                    {
                        int total = 0, errs = 0, warns = 0;
                        try
                        {
                            var msgs = sys3.get_message_objects(std, (_3S.CoDeSys.Core.Messages.Severity)31);
                            if (msgs != null)
                            {
                                total = msgs.Count;
                                foreach (var m in msgs)
                                {
                                    string sev = m.severity.ToString();
                                    if (sev.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0 || sev.IndexOf("Fatal", StringComparison.OrdinalIgnoreCase) >= 0) errs++;
                                    else if (sev.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0) warns++;
                                }
                            }
                        }
                        catch { }

                        result.categories.Add(new MessageCategoryInfo
                        {
                            name = std,
                            guid = string.Empty,
                            totalMessages = total,
                            errors = errs,
                            warnings = warns
                        });
                    }
                }

                return result;
            });
        }

        public static List<string> GetIdeMessages(string category = null)
        {
            return CodesysContext.GetIdeMessages(category);
        }
    }
}
