using System.Security;
using System.Text.RegularExpressions;

namespace RPG.Server;

internal static partial class CharacterHandleValidator
{
    private const string HandlePattern = "^[a-zA-Z0-9_]{3,20}$";

    public static string ValidateAndNormalize(string handle, string parameterName = "handle")
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            throw new ArgumentException($"{parameterName} cannot be null or empty.", parameterName);
        }

        string safeHandle = Path.GetFileName(handle);
        if (safeHandle != handle || string.IsNullOrWhiteSpace(safeHandle))
        {
            throw new ArgumentException("Invalid character handle: contains invalid path characters.", parameterName);
        }

        if (!Regex.IsMatch(safeHandle, HandlePattern))
        {
            throw new ArgumentException("Invalid character handle: must be 3-20 alphanumeric characters or underscores.", parameterName);
        }

        return safeHandle;
    }

    public static string ResolveCharacterFilePath(string handle, string directoryPath)
    {
        string safeHandle = ValidateAndNormalize(handle);

        string absoluteDir = DataPaths.Resolve(directoryPath);
        Directory.CreateDirectory(absoluteDir);

        string fullFilePath = Path.GetFullPath(Path.Combine(absoluteDir, $"{safeHandle}.json"));
        string fullAbsoluteDir = Path.GetFullPath(absoluteDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullFilePath.StartsWith(fullAbsoluteDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("Invalid character handle: Path traversal detected.");
        }

        return fullFilePath;
    }
}
