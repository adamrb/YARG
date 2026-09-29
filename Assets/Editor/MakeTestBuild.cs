using System;
using System.Linq;
using UnityEditor;

namespace Editor
{
    public static class MakeTestBuild
    {
        private const string YARG_TEST_BUILD = "YARG_TEST_BUILD";
        private const string YARG_NIGHTLY_BUILD = "YARG_NIGHTLY_BUILD";

        [MenuItem("File/Make Test Build", false, 220)]
        public static void MakeTestBuildClicked()
        {
            MakeBuild(YARG_TEST_BUILD);
        }

        [MenuItem("File/Make Nightly Build", false, 220)]
        public static void MakeNightlyBuildClicked()
        {
            MakeBuild(YARG_NIGHTLY_BUILD);
        }

        public static void MakeBuild(string defineSymbol)
        {
            // Get build settings
            var buildSettings = BuildPlayerWindow.DefaultBuildMethods.GetBuildPlayerOptions(default);

            // Get current defines
            // TODO: BuildTargetGroup is slated for deprecation, figure out how to do this with NamedBuildTarget instead
            var namedBuildTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
            PlayerSettings.GetScriptingDefineSymbols(namedBuildTarget, out var originalDefines);
            originalDefines ??= Array.Empty<string>();

            // Set test build define
            var buildDefines = buildSettings.extraScriptingDefines ?? Array.Empty<string>();
            if (!originalDefines.Contains(defineSymbol) && !buildDefines.Contains(defineSymbol))
            {
                ArrayUtility.Add(ref buildDefines, defineSymbol);
            }

            buildSettings.extraScriptingDefines = buildDefines;

            // Build the player
            BuildPipeline.BuildPlayer(buildSettings);
        }

        /// <summary>
        /// Headless entry point for <c>-executeMethod Editor.MakeTestBuild.BuildLinuxTestFromCommandLine</c>.
        /// Output path can be set with <c>-buildOutput &lt;path&gt;</c>.
        /// </summary>
        public static void BuildLinuxTestFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            int outputIndex = Array.IndexOf(args, "-buildOutput");
            string output = outputIndex >= 0 && outputIndex + 1 < args.Length
                ? args[outputIndex + 1]
                : "Builds/Linux/YARG.x86_64";

            var options = new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes
                    .Where(scene => scene.enabled)
                    .Select(scene => scene.path)
                    .ToArray(),
                locationPathName = output,
                target = BuildTarget.StandaloneLinux64,
                extraScriptingDefines = new[] { YARG_TEST_BUILD },
            };

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }
    }
}