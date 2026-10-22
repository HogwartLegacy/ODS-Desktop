using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Ods.Core.Platform;

/// <summary>
/// Current-user-only files for credentials (port of
/// installers/windows/lib/private-file.ps1). The DACL is supplied when the
/// staging file is created, so even an empty file never inherits read access
/// for other users, then the staged file atomically replaces the target.
/// </summary>
public static class PrivateFile
{
    public static void WriteBytes(string path, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("A private file needs a parent directory.", nameof(path));
        Directory.CreateDirectory(parent);
        var existed = File.Exists(path);
        if (existed) Protect(path);

        var temporary = Path.Combine(parent, ".ods-private-" + Guid.NewGuid().ToString("N"));
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        try
        {
            using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
            {
                VerifyOwnerOnly(temporary);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (existed) File.Replace(temporary, path, destinationBackupFileName: null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Restrict an existing regular file to the current user and verify the result.</summary>
    public static void Protect(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidOperationException($"The ODS credential file {path} must be a regular file.");
        }
        var acl = info.GetAccessControl(AccessControlSections.Access);
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            acl.RemoveAccessRuleSpecific(rule);
        }
        acl.SetAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(acl);
        VerifyOwnerOnly(path);
    }

    private static void VerifyOwnerOnly(string path)
    {
        var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        if (!acl.AreAccessRulesProtected
            || rules.Count != 1
            || !rules[0].IdentityReference.Equals(CurrentUser)
            || rules[0].IsInherited
            || rules[0].AccessControlType != AccessControlType.Allow
            || rules[0].FileSystemRights != FileSystemRights.FullControl)
        {
            throw new InvalidOperationException($"Could not verify current-user-only access to {path}.");
        }
    }

    private static SecurityIdentifier CurrentUser =>
        WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows user has no SID.");
}

/// <summary>
/// The llama-server API key: 64 lowercase hex characters in an owner-only
/// file, read by llama-server through --api-key-file so it never appears on a
/// command line. llama.cpp reads the file with std::getline, so it holds no
/// BOM and no newline.
/// </summary>
public static class ApiKeyFile
{
    public static string EnsureKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = Read(path);
            PrivateFile.Protect(path);
            return existing;
        }
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        PrivateFile.WriteBytes(path, Encoding.ASCII.GetBytes(key));
        return key;
    }

    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.ASCII.GetString(bytes);
        if (bytes.Length != 64 || !text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException(
                $"The llama-server API key file {path} is malformed (it must hold 64 hex characters, no BOM or newline). Delete it and restart ODS to create a new key.");
        }
        return text;
    }
}
