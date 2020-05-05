// --------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// --------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Microsoft.Terrapin.Worker.PrebuildLogic
{
    /// <summary>
    /// A class that contains the logic need to execute before starting a build for an NPM package.
    /// The program looks for the files in the directory in which it was invoked, not necessarily where the prebuild logic is.
    /// </summary>
    public static class PrebuildNpm
    {
        public const string LockFileName = "package-lock.json";
        public const string PackageFileName = "package.json";
        public const string NpmrcFileName = ".npmrc";
        public const string IntegrityField = "integrity";
        public const string PrivateField = "private";
        public const string SectionSeparator = "######";
        public const string ExceptionSeparator = "#!#!#!";

        /// <summary>
        /// Remove `integrity` properties from all dependencies and subdependencies in the package-lock file to unblock Terrapin builds.
        /// </summary>
        /// <returns>Total number of `integrity` properties that have been cleaned out.</returns>
        public static async Task<int> CleanIntegrityFieldsAsync()
        {
            try
            {
                var lockFileText = await File.ReadAllTextAsync(LockFileName);

                var lockData = JsonSerializer.Deserialize<Dictionary<string, object>>(lockFileText);

                // The original object could contain an integrity field as well and would need the same treatment as its deps.
                int totalDeleted = CleanIntegrityRecursively(lockData);

                await File.WriteAllTextAsync(LockFileName, JsonSerializer.Serialize(lockData));
                Console.WriteLine($"{SectionSeparator} Cleaned {totalDeleted} '{IntegrityField}' fields.");
                return totalDeleted;
            }
            catch (Exception exc)
            {
                Console.WriteLine($"{ExceptionSeparator} {exc.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Removes the field `private` from a package.json file because it prevents us from running `npm publish` on the resulting tarball.
        /// </summary>
        public static async Task RemovePrivateField()
        {
            try
            {
                var pkgTxt = await File.ReadAllTextAsync(PackageFileName);
                var packageData = JsonSerializer.Deserialize<Dictionary<string, object>>(pkgTxt);
                if (!packageData.ContainsKey(PrivateField))
                {
                    Console.WriteLine($"{SectionSeparator} {PackageFileName} does not contain '{PrivateField}' field.");
                    return;
                }

                packageData.Remove(PrivateField);
                await File.WriteAllTextAsync(PackageFileName, JsonSerializer.Serialize(packageData));
                Console.WriteLine($"{SectionSeparator} Removed '{PrivateField}' field from {PackageFileName}.");
            }
            catch (Exception exc)
            {
                Console.WriteLine($"{ExceptionSeparator} {exc.Message}");
            }
        }

        /// <summary>
        /// A method to clean the .npmrc file in case it contains 'package-lock=false'.
        /// If this parameter is set, it prevents Terrapin from executing validations properly.
        /// </summary>
        public static async Task RemovePackageLockFalse()
        {
            try
            {
                const string packageLockFalse = "package-lock=false";
                const string packageLockFalseRx = @"package-lock\s?=\s?false\r?\n?";
                Regex rx = new Regex(packageLockFalseRx, RegexOptions.Compiled);

                Console.WriteLine($"Checking if {NpmrcFileName} contains '{packageLockFalse}'.");

                var fileText = await File.ReadAllTextAsync(NpmrcFileName);
                var matches = rx.Matches(fileText);

                if (matches.Count == 0)
                {
                    Console.WriteLine($"{SectionSeparator} {NpmrcFileName} did not contain {packageLockFalse}.");
                    return;
                }

                Console.WriteLine($"Trying to remove '{packageLockFalse}'");

                var cleanText = rx.Replace(fileText, string.Empty);
                await File.WriteAllTextAsync(NpmrcFileName, cleanText);

                Console.WriteLine($"{SectionSeparator} Successfully rewrote {NpmrcFileName} without {packageLockFalse}.");
            }
            catch (Exception exc)
            {
                Console.WriteLine($"{ExceptionSeparator} {exc.Message}");
            }
        }

        /// <summary>
        /// A method to orchastrate all the required pre-build steps for NPM packages.
        /// </summary>
        public static async Task Main()
        {
            if (File.Exists(LockFileName))
            {
                Console.WriteLine($"{SectionSeparator} Cleaning {LockFileName} from all '{IntegrityField}' fields, to allow Terrapin - built artifacts to be consumed");
                await CleanIntegrityFieldsAsync();
            }
            else
            {
                Console.WriteLine($"{SectionSeparator} {LockFileName} file not found");
            }

            if (File.Exists(PackageFileName))
            {
                Console.WriteLine($"{SectionSeparator} Removing {PrivateField} from {PackageFileName}, to resolve the build issues caused by private packages");
                await RemovePrivateField();
            }
            else
            {
                Console.WriteLine($"{SectionSeparator} {PackageFileName} file not found");
            }

            if (File.Exists(NpmrcFileName))
            {
                Console.WriteLine($"{SectionSeparator} Found {NpmrcFileName}, attempting a cleanup...");
                await RemovePackageLockFalse();
            }
            else
            {
                Console.WriteLine($"{SectionSeparator} {NpmrcFileName} file not found");
            }
        }

        /// <summary>
        /// Visits all dependencies recursively and cleans-up `integrity` properties.
        /// </summary>
        /// <param name="dep">The current dependency being processed.</param>
        /// <returns>Number of `integrity` properties cleaned-up in this sub-tree.</returns>
        private static int CleanIntegrityRecursively(Dictionary<string, object> dep)
        {
            const string dependencies = "dependencies";

            int deletedCount = TryDeleteIntegrityField(dep) ? 1 : 0;

            if (dep.ContainsKey(dependencies))
            {
                /* The default JsonSerializer.Deserialize output object is a JsonObject with JsonElements. These are immutable.
                 * In order to be able to perform clean-up of unneeded properties, the dependencies are re-serialized into
                 * vanilla Dictionary objects.
                 * */
                var childDeps = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, object>>>(dep[dependencies].ToString());
                dep[dependencies] = childDeps;

                foreach (var child in childDeps.Values)
                {
                    deletedCount += CleanIntegrityRecursively(child);
                }
            }

            return deletedCount;
        }

        /// <summary>
        /// Helper method to remove an `integrity` property from a dependency object.
        /// </summary>
        /// <param name="dep">The dependency that needs cleaning.</param>
        /// <returns>Whehter clean-up happened or not.</returns>
        private static bool TryDeleteIntegrityField(Dictionary<string, object> dep)
        {
            if (dep.ContainsKey(IntegrityField))
            {
                dep.Remove(IntegrityField);
                return true;
            }

            return false;
        }
    }
}
