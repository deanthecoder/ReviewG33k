// Code authored by Dean Edis (DeanTheCoder).
// Anyone is free to copy, modify, use, compile, or distribute this software,
// either in source code form or as a compiled binary, for any purpose.
//
// If you modify the code, please retain this copyright header,
// and consider contributing back to the repository or letting us know
// about your modifications. Your contributions are valued!
//
// THE SOFTWARE IS PROVIDED AS IS, WITHOUT WARRANTY OF ANY KIND.

namespace ReviewG33k.Services.Checks;

public static class CodeReviewRuleIds
{
    public const string AsyncVoid = "async-void";
    public const string AsyncMethodNameSuffix = "async-method-name-suffix";
    public const string PublicMethodArgumentGuards = "public-method-argument-guards";
    public const string EmptyCatch = "empty-catch";
    public const string SwallowingCatch = "swallowing-catch";
    public const string LockThisOrPublic = "lock-this-or-public";
    public const string TaskRunAsync = "task-run-async";
    public const string MissingXmlDocs = "missing-xml-docs";
    public const string MissingTests = "missing-tests";
    public const string MissingTestsForPublicMethods = "missing-tests-public-methods";
    public const string PropertyCanBeAutoProperty = "property-can-be-auto-property";
    public const string PrivatePropertyShouldBeField = "private-property-should-be-field";
    public const string PrivateFieldCanBeReadonly = "private-field-can-be-readonly";
    public const string MethodParameterCount = "method-parameter-count";
    public const string GenericTypeNameSuffix = "generic-type-name-suffix";
    public const string IfElseBraceConsistency = "if-else-brace-consistency";
    public const string ConstructorTooLong = "constructor-too-long";
    public const string ThreadSleep = "thread-sleep";
    public const string ThrowExInCatch = "throw-ex-in-catch";
    public const string MethodCanBeStatic = "method-can-be-static";
    public const string UnnecessaryCast = "unnecessary-cast";
    public const string UnnecessaryVerbatimStringPrefix = "unnecessary-verbatim-string-prefix";
    public const string UnobservedTaskResult = "unobserved-task-result";
    public const string DisposableNotDisposed = "disposable-not-disposed";
    public const string MultipleEnumeration = "multiple-enumeration";
    public const string PublicMutableStaticState = "public-mutable-static-state";
    public const string UnusedPrivateMember = "unused-private-member";
    public const string UnusedUsingsRoslyn = "unused-usings-roslyn";
    public const string WarningSuppression = "warning-suppression";
}
