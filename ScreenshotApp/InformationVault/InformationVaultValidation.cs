using System.IO;

namespace ScreenshotApp.InformationVault;

/// <summary>仅由显式界面验证参数调用，使用虚假数据验证加密往返与错误密码路径。</summary>
internal static class InformationVaultValidation
{
    internal static InformationVaultValidationResult Run(string outputDirectory)
    {
        var vaultPath = Path.Combine(outputDirectory, "information-vault-test.dat");
        if (File.Exists(vaultPath))
        {
            File.Delete(vaultPath);
        }

        const string password = "XTool-测试主密码-2026";
        const string fakeAccount = "validation@example.invalid";
        const string fakeSecret = "validation-secret-not-real";
        using var store = new InformationVaultStore(vaultPath);
        var data = store.Create(password);
        data.Entries.Add(new InformationVaultEntry
        {
            Type = InformationVaultEntryType.Steam,
            Title = "验证用 Steam 记录",
            Account = fakeAccount,
            Secret = fakeSecret
        });
        data.Entries.Add(new InformationVaultEntry
        {
            Type = InformationVaultEntryType.RecoveryCodes,
            Title = "旧版 GitHub 恢复码",
            RecoveryCodes = [new InformationVaultRecoveryCode { Value = "LEGACY-VALIDATION-CODE" }]
        });
        store.Save(data);
        var encryptedFile = File.ReadAllText(vaultPath);
        var plaintextAbsent = !encryptedFile.Contains(fakeAccount, StringComparison.Ordinal) &&
                              !encryptedFile.Contains(fakeSecret, StringComparison.Ordinal) &&
                              !encryptedFile.Contains("验证用 Steam 记录", StringComparison.Ordinal);

        store.Lock();
        var wrongPasswordRejected = false;
        try
        {
            _ = store.Unlock("错误密码-绝不会成功");
        }
        catch (InformationVaultPasswordException)
        {
            wrongPasswordRejected = true;
        }

        var loaded = store.Unlock(password);
        var legacyRecoveryCodesMigrated = loaded.Entries.Count == 2 &&
                                          loaded.Entries[1].Type == InformationVaultEntryType.GitHubCredential &&
                                          loaded.Entries[1].RecoveryCodes.Count == 1;
        var firstRoundTripSucceeded = loaded.Entries.Count == 2 &&
                                      loaded.Entries[0].Account == fakeAccount &&
                                      loaded.Entries[0].Secret == fakeSecret;
        loaded.Entries[0].Notes = "解锁后再次保存";
        store.Save(loaded);
        store.Lock();
        var loadedAgain = store.Unlock(password);
        var roundTripSucceeded = firstRoundTripSucceeded &&
                                 loadedAgain.Entries.Count == 2 &&
                                 loadedAgain.Entries[0].Notes == "解锁后再次保存";
        store.Reset();
        var resetRemovedVault = !File.Exists(vaultPath);
        if (!plaintextAbsent || !wrongPasswordRejected || !roundTripSucceeded ||
            !legacyRecoveryCodesMigrated || !resetRemovedVault)
        {
            throw new InvalidOperationException("信息库加密验证未通过。请检查明文泄漏、错误密码或重置路径。");
        }

        return new InformationVaultValidationResult(
            PlaintextAbsent: plaintextAbsent,
            WrongPasswordRejected: wrongPasswordRejected,
            RoundTripSucceeded: roundTripSucceeded,
            LegacyRecoveryCodesMigrated: legacyRecoveryCodesMigrated,
            ResetRemovedVault: resetRemovedVault);
    }
}

internal sealed record InformationVaultValidationResult(
    bool PlaintextAbsent,
    bool WrongPasswordRejected,
    bool RoundTripSucceeded,
    bool LegacyRecoveryCodesMigrated,
    bool ResetRemovedVault);
