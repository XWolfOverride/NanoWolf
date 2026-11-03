namespace XWolf.NanoWolf.Tests;

/// <summary>Every startup error must stop the application with a message that says where the problem is.</summary>
public class CatalogTests
{
    private static string[] Errors(params Type[] types)
        => Assert.Throws<CatalogException>(() => CallCatalog.FromTypes(types)).Errors.ToArray();

    // ---- valid ----

    [Fact]
    public void Builds_a_valid_catalog()
    {
        var catalog = CallCatalog.FromTypes([typeof(ItemCalls), typeof(UtilCalls), typeof(SharedCalls)]);

        Assert.NotNull(catalog.Find("items", "get"));
        Assert.NotNull(catalog.Find("items", "edit")); // EditAsync -> edit
        Assert.NotNull(catalog.Find("util", "echo")); // explicit group on a static class
        Assert.Null(catalog.Find("items", "Get")); // lookup is exact
        Assert.Null(catalog.Find("items", "nope"));
    }

    [Fact]
    public void Hash_is_stable_and_changes_with_the_contract()
    {
        var a = CallCatalog.FromTypes([typeof(ItemCalls)]);
        var b = CallCatalog.FromTypes([typeof(ItemCalls)]);
        var c = CallCatalog.FromTypes([typeof(ItemCalls), typeof(UtilCalls)]);

        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
    }

    [Fact]
    public void Access_defaults_to_user()
    {
        var catalog = CallCatalog.FromTypes([typeof(ItemCalls)]);

        Assert.Equal(Access.User, catalog.Find("items", "edit")!.Access);
        Assert.Equal(Access.Admin, catalog.Find("items", "secret")!.Access);
    }

    [Fact]
    public void Methods_without_the_attribute_are_not_published()
    {
        var catalog = CallCatalog.FromTypes([typeof(Mixed)]);

        Assert.Single(catalog.Calls);
        Assert.Null(catalog.Find("mixed", "hidden"));
    }

    // ---- errors ----

    [Fact]
    public void Two_calls_with_the_same_group_and_name_clash()
    {
        var errors = Errors(typeof(DupA), typeof(DupB));

        var message = Assert.Single(errors);
        Assert.Contains("dup.same", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(DupA), message);
        Assert.Contains(nameof(DupB), message);
    }

    [Fact]
    public void Names_that_differ_only_in_case_clash()
    {
        Assert.Single(Errors(typeof(CaseA), typeof(CaseB)));
    }

    [Fact]
    public void Overloads_clash()
    {
        Assert.Single(Errors(typeof(Overloaded)));
    }

    [Fact]
    public void A_call_without_group_is_an_error()
    {
        Assert.Contains(Errors(typeof(NoGroup)), e => e.Contains("no group") && e.Contains(nameof(NoGroup)));
    }

    [Fact]
    public void Reserved_javascript_names_are_rejected()
    {
        var errors = Errors(typeof(ReservedGroup), typeof(ReservedName));

        Assert.Equal(2, errors.Length);
        Assert.All(errors, e => Assert.Contains("reserved", e));
    }

    [Fact]
    public void Invalid_names_are_rejected()
    {
        Assert.Contains(Errors(typeof(BadName)), e => e.Contains("invalid name"));
    }

    [Fact]
    public void Get_calls_must_be_read_only()
    {
        Assert.Contains(Errors(typeof(GetNotReadOnly)), e => e.Contains("ReadOnly"));
    }

    [Fact]
    public void Get_calls_only_accept_simple_parameters()
    {
        Assert.Contains(Errors(typeof(GetComplex)), e => e.Contains("GET parameters"));
    }

    [Fact]
    public void ValueTask_is_rejected()
    {
        Assert.Contains(Errors(typeof(UsesValueTask)), e => e.Contains("ValueTask"));
    }

    [Fact]
    public void CancellationToken_parameters_are_rejected()
    {
        Assert.Contains(Errors(typeof(UsesCancellation)), e => e.Contains("Ctx.Cancel"));
    }

    [Fact]
    public void Only_one_Ctx_is_allowed()
    {
        Assert.Contains(Errors(typeof(TwoCtx)), e => e.Contains("more than one Ctx"));
    }

    [Fact]
    public void Unsupported_parameter_types_are_rejected()
    {
        var errors = Errors(typeof(Unsupported));

        Assert.Equal(2, errors.Length); // dictionary and object
    }

    [Fact]
    public void Recursive_parameter_types_are_rejected()
    {
        Assert.Contains(Errors(typeof(Recursive)), e => e.Contains("recursive"));
    }

    [Fact]
    public void Published_methods_must_be_public()
    {
        Assert.Contains(Errors(typeof(NotPublic)), e => e.Contains("public"));
    }

    [Fact]
    public void Instance_methods_of_abstract_classes_are_rejected()
    {
        Assert.Contains(Errors(typeof(AbstractCalls)), e => e.Contains("concrete"));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var errors = Errors(typeof(ManyProblems));

        Assert.True(errors.Length >= 3, string.Join(Environment.NewLine, errors));
    }

    // ---- fixtures ----

    [CallGroup("mixed")]
    public class Mixed
    {
        [Call] public string Shown() => "";
        public string Hidden() => "";
    }

    [CallGroup("dup")]
    public class DupA { [Call(Name = "same")] public int A() => 1; }

    [CallGroup("dup")]
    public class DupB { [Call(Name = "same")] public int B() => 1; }

    [CallGroup("case")]
    public class CaseA { [Call(Name = "thing")] public int A() => 1; }

    [CallGroup("case")]
    public class CaseB { [Call(Name = "THING")] public int B() => 1; }

    [CallGroup("over")]
    public class Overloaded
    {
        [Call] public int Load(string a) => 1;
        [Call] public int Load(int a) => 1;
    }

    public class NoGroup { [Call] public int Run() => 1; }

    [CallGroup("constructor")]
    public class ReservedGroup { [Call] public int Run() => 1; }

    [CallGroup("ok")]
    public class ReservedName { [Call(Name = "prototype")] public int Run() => 1; }

    [CallGroup("bad")]
    public class BadName { [Call(Name = "my-call")] public int Run() => 1; }

    [CallGroup("g")]
    public class GetNotReadOnly { [Call(Verb = Verb.Get)] public int Run() => 1; }

    public record Complex(string A);

    [CallGroup("g")]
    public class GetComplex { [Call(Verb = Verb.Get, ReadOnly = true)] public int Run(Complex c) => 1; }

    [CallGroup("g")]
    public class UsesValueTask { [Call] public ValueTask<int> Run() => ValueTask.FromResult(1); }

    [CallGroup("g")]
    public class UsesCancellation { [Call] public Task Run(CancellationToken token) => Task.CompletedTask; }

    [CallGroup("g")]
    public class TwoCtx { [Call] public int Run(Ctx a, Ctx b) => 1; }

    [CallGroup("g")]
    public class Unsupported
    {
        [Call] public int Run(Dictionary<string, int> map) => 1;
        [Call] public int Other(object any) => 1;
    }

    public class Node { public Node? Next { get; set; } }

    [CallGroup("g")]
    public class Recursive { [Call] public int Run(Node node) => 1; }

    [CallGroup("g")]
    public class NotPublic { [Call] internal int Run() => 1; }

    [CallGroup("g")]
    public abstract class AbstractCalls { [Call] public int Run() => 1; }

    public class ManyProblems
    {
        [Call] public int NoGroupHere() => 1;
        [Call(Group = "g", Verb = Verb.Get)] public int NotReadOnly() => 1;
        [Call(Group = "g")] public ValueTask<int> Value() => ValueTask.FromResult(1);
    }
}
