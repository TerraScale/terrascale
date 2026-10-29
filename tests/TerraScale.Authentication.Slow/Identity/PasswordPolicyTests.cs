using TerraScale.Authentication.Features.Identity;

namespace TerraScale.Authentication.Slow.Identity;

/// <summary>The new-password policy at its boundaries: Unicode length, common and repetitive choices, the own email.</summary>
public sealed class PasswordPolicyTests
{
    [Test]
    [Arguments("fourteen chars")]
    [Arguments("ééééééééééééé1")]
    [Arguments("🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑🔑x")]
    [Arguments("")]
    public async Task RefusesShortOrMalformed(string password) =>
        await Assert.That(PasswordPolicy.Check(password, null)).IsEqualTo(PasswordProblem.Length);

    [Test]
    public async Task CountsCodePointsNotBytes()
    {
        // 15 code points: 29 UTF-16 units and 57 UTF-8 bytes, but still exactly the minimum.
        await Assert.That(PasswordPolicy.Check("🔑🌲🚀🎲🐙🧭🪐🌋🦉🍋🎻🧊🛶🌵x", null)).IsEqualTo(PasswordProblem.None);
        await Assert.That(PasswordPolicy.Check("ünïcödé passwörd", null)).IsEqualTo(PasswordProblem.None);
        await Assert.That(PasswordPolicy.Check("\ud83d passphrase of words", null)).IsEqualTo(PasswordProblem.Length);
        await Assert.That(PasswordPolicy.Check(new string('ж', PasswordPolicy.MaxCharacters - 1) + "q", null)).IsEqualTo(PasswordProblem.None);
        await Assert.That(PasswordPolicy.Check(new string('ж', PasswordPolicy.MaxCharacters) + "q", null)).IsEqualTo(PasswordProblem.Length);
    }

    [Test]
    [Arguments("1234567890123456")]
    [Arguments("QwertyUiopAsdfgh")]
    [Arguments("ＰＡＳＳＷＯＲＤＰＡＳＳＷＯＲＤ")]
    [Arguments("aaaaaaaaaaaaaaaaaaaa")]
    [Arguments("iloveyouiloveyou")]
    [Arguments("abcabcabcabcabcabc")]
    public async Task RefusesCommonAndRepetitive(string password) =>
        await Assert.That(PasswordPolicy.Check(password, null)).IsEqualTo(PasswordProblem.Common);

    [Test]
    public async Task RefusesTheOwnEmail()
    {
        await Assert.That(PasswordPolicy.Check("Long.Name@Example.test", "long.name@example.test")).IsEqualTo(PasswordProblem.Common);
        await Assert.That(PasswordPolicy.Check("averylonglocalpart", "averylonglocalpart@example.test")).IsEqualTo(PasswordProblem.Common);
        await Assert.That(PasswordPolicy.Check("averylonglocalpart", "someone@example.test")).IsEqualTo(PasswordProblem.None);
    }

    [Test]
    public async Task SignInOnlyNeedsAVerifiablePassword()
    {
        await Assert.That(PasswordPolicy.Verifiable("short")).IsTrue();
        await Assert.That(PasswordPolicy.Verifiable(string.Empty)).IsFalse();
        await Assert.That(PasswordPolicy.Verifiable("\udc00 lone surrogate")).IsFalse();
        await Assert.That(PasswordPolicy.Verifiable(new string('x', PasswordPolicy.MaxCharacters + 1))).IsFalse();
    }
}
