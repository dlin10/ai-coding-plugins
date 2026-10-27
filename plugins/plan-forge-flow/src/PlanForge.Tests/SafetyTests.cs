using PlanForge.Prompts;
using PlanForge.Review;
using PlanForge.Run;
using PlanForge.Vendors;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// The two checks that survived the move to out-of-process workers: nothing secret leaves for a
/// vendor, and nothing is written outside the run folder.
/// </summary>
public sealed class SafetyTests
{
    [Theory]
    [InlineData(".env")]
    [InlineData("config/.env.production")]
    [InlineData("deploy/id_rsa")]
    [InlineData("certs/server.pem")]
    [InlineData("src/appsettings.Production.json")]
    [InlineData("infra/terraform.tfstate")]
    public void Names_a_sensitive_path(string path) => Assert.True(SensitiveInput.IsSensitivePath(path));

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("docs/adr/0002-mcp-server-surface-without-enforcement.md")]
    public void Leaves_an_ordinary_path_alone(string path) => Assert.False(SensitiveInput.IsSensitivePath(path));

    /// <summary>
    /// Issue #88's false positive, on paths: a keyword anywhere in a changed path refused the whole
    /// code-review round, and a type named after a cancellation token or a password policy is
    /// ordinary code whose lines still reach the content rule. So is a snake_case module, which is
    /// why the keyword is not narrowed to a whole word instead — <c>token_cache</c> is one.
    /// </summary>
    [Theory]
    [InlineData("src/Infra/CancellationTokenExtensions.cs")]
    [InlineData("tests/PasswordPolicyTests.cs")]
    [InlineData("src/Auth/TokenCache.cs")]
    [InlineData("app/auth/token_cache.py")]
    public void Leaves_a_source_file_named_after_a_secret_keyword_alone(string path) =>
        Assert.False(SensitiveInput.IsSensitivePath(path));

    /// <summary>
    /// The other half of the same change: a file that is not source code keeps the keyword rule,
    /// because a secret there can sit on a line with no name beside it and the path is all there is
    /// to go on. A keyword in a directory still counts.
    /// </summary>
    [Theory]
    [InlineData("config/tokens.json")]
    [InlineData("secret.txt")]
    [InlineData(".git-credentials")]
    [InlineData("password.txt")]
    [InlineData("deploy/api-token")]
    [InlineData("credentials")]
    [InlineData("secrets/prod.json")]
    public void Still_names_a_file_named_for_the_secret_it_holds(string path) =>
        Assert.True(SensitiveInput.IsSensitivePath(path));

    /// <summary>
    /// The same false positive from the SSH-key rule: <c>id_</c> opens <c>id_generator.py</c> as
    /// readily as <c>id_rsa</c>, and either name refused the whole code-review round.
    /// </summary>
    [Theory]
    [InlineData("src/id_generator.py")]
    [InlineData("src/Users/id_mapping.cs")]
    [InlineData("tests/id_generator_test.go")]
    public void Leaves_a_source_file_that_starts_like_an_ssh_key_alone(string path) =>
        Assert.False(SensitiveInput.IsSensitivePath(path));

    /// <summary>
    /// The other half: a key keeps its guard whatever algorithm, suffix or backup extension its
    /// name carries, and so does its public half.
    /// </summary>
    [Theory]
    [InlineData("id_rsa")]
    [InlineData(".ssh/id_ed25519")]
    [InlineData("deploy/id_ecdsa")]
    [InlineData("keys/id_github")]
    [InlineData("backup/id_rsa.bak")]
    [InlineData("id_rsa.pub")]
    public void Still_names_an_ssh_key(string path) => Assert.True(SensitiveInput.IsSensitivePath(path));

    /// <summary>
    /// Issue #200: the name of a JSON or YAML file says what it is about — a library called
    /// <c>UserSecrets</c>, a workflow that rotates tokens — so its lines are read instead of the name
    /// refusing the round. The name still names it, which is all <c>IsSensitivePath</c> answers.
    /// </summary>
    /// <param name="path">A changed JSON or YAML path marked sensitive by a keyword alone.</param>
    [Theory]
    [InlineData("plugins/concurrency-hunter/demo/.concurrency-hunter/models/generated/Microsoft.AspNetCore.App/10.0/Microsoft.Extensions.Configuration.UserSecrets.json")]
    [InlineData("models/System.IdentityModel.Tokens.Jwt.json")]
    [InlineData("config/tokens.json")]
    [InlineData("secrets/prod.json")]
    [InlineData(".github/workflows/rotate-tokens.yml")]
    [InlineData("config/Password-Rules.YAML")]
    [InlineData("src/id_mapping.json")]
    [InlineData("config\\tokens.json")]
    public void Reads_a_json_or_yaml_file_named_for_a_secret_instead_of_refusing_it(string path)
    {
        Assert.True(SensitiveInput.IsSensitivePath(path));
        Assert.True(SensitiveInput.IsDecidedByContent(path));
    }

    /// <summary>
    /// The other half: what is not JSON or YAML keeps the name as the verdict — XML included, which
    /// neither rule can read — and so does a file known to hold secrets, key material and a sensitive
    /// location, whatever its extension.
    /// </summary>
    /// <param name="path">A changed path marked sensitive.</param>
    [Theory]
    [InlineData("secret.txt")]
    [InlineData("password.txt")]
    [InlineData(".git-credentials")]
    [InlineData("deploy/api-token")]
    [InlineData("credentials")]
    [InlineData("docs/token-rotation.md")]
    [InlineData("lib/Microsoft.Extensions.Configuration.UserSecrets.xml")]
    [InlineData("config/secrets.json")]
    [InlineData("deploy/secret.yaml")]
    [InlineData("src/appsettings.Production.json")]
    [InlineData("deploy/service-account.json")]
    [InlineData("certs/token.pem")]
    [InlineData("keys/id_rsa.pub")]
    public void Keeps_the_name_as_the_verdict_for_other_files_and_known_secret_files(string path)
    {
        Assert.True(SensitiveInput.IsSensitivePath(path));
        Assert.False(SensitiveInput.IsDecidedByContent(path));
    }

    /// <summary>A path the name does not mark has nothing to decide: JSON alone is not enough.</summary>
    /// <param name="path">A changed path the name does not mark.</param>
    [Theory]
    [InlineData("config/app.json")]
    [InlineData("src/Program.cs")]
    [InlineData("docs/adr/0002-mcp-server-surface-without-enforcement.md")]
    public void Has_nothing_to_decide_for_a_path_the_name_does_not_mark(string path)
    {
        Assert.False(SensitiveInput.IsSensitivePath(path));
        Assert.False(SensitiveInput.IsDecidedByContent(path));
    }

    /// <summary>
    /// The change that filed issue #200, as git renders it: the generator hash of a generated model, a
    /// 64-digit hex string beside a key, which a rule that withheld a long hex value wherever it
    /// stands would refuse.
    /// </summary>
    [Fact]
    public void Finds_nothing_in_the_one_line_change_to_a_generated_model()
    {
        var diff = """
                   diff --git a/models/Microsoft.Extensions.Configuration.UserSecrets.json b/models/Microsoft.Extensions.Configuration.UserSecrets.json
                   --- a/models/Microsoft.Extensions.Configuration.UserSecrets.json
                   +++ b/models/Microsoft.Extensions.Configuration.UserSecrets.json
                   @@ -1,6 +1,6 @@
                    {
                      "schemaVersion": 2,
                   -  "generator": "94d7f8f3ef2ad5d446da8d0a822f46ead8691190a95f1a40a3f02b03ff80f748",
                   +  "generator": "e67eee3e02f7314f0fad7f17243ea570c8bea8d9af0e36df7012b0d1cb9171c4",
                      "assembly": {
                        "name": "Microsoft.Extensions.Configuration.UserSecrets",
                        "version": "10.0.0.0",
                   """;

        Assert.Null(SensitiveInput.FindInStructuredFile(diff));
    }

    /// <summary>
    /// The same kind of model written fresh, as a run that creates one does: every line is an
    /// addition. What the rule must pass is here — a hash, a GUID and a member id beside keys, and
    /// names that stand alone in an array, kebab-case words and a dotted identifier.
    /// </summary>
    [Fact]
    public void Finds_nothing_in_a_generated_model_written_as_a_new_file()
    {
        var model = """
                    {
                      "generator": "94d7f8f3ef2ad5d446da8d0a822f46ead8691190a95f1a40a3f02b03ff80f748",
                      "assembly": {
                        "name": "Microsoft.Extensions.Configuration.UserSecrets",
                        "mvid": "601a02d3-9487-45ce-a28b-99b5cb103efd"
                      },
                      "members": [
                        {
                          "member": "M:System.Buffers.ReadOnlySequence`1.get_IsSingleSegment",
                          "refusal": {
                            "causes": [
                              "argument-member-unseen",
                              "probe-handed-to-unseen",
                              "witness-value-handed",
                              "Microsoft.Extensions.Configuration.UserSecrets"
                            ]
                          }
                        }
                      ]
                    }
                    """;

        Assert.Null(SensitiveInput.FindInStructuredFile(AsNewFile("models/UserSecrets.json", model)));
    }

    /// <summary>
    /// What a name stands alone for is not a secret: a URL has a colon, a sentence has spaces, a short
    /// word is short, and a kebab-case name has two kinds of character.
    /// </summary>
    [Fact]
    public void Leaves_a_url_a_sentence_and_a_name_standing_alone_alone()
    {
        var list = "urls:\n"
                   + "  - https://example.com/a1B2c3D4e5F6g7H8i9J0\n"
                   + "  - a sentence of plain words standing here\n"
                   + "  - argument-member-unseen\n"
                   + "  - short\n";

        Assert.Null(SensitiveInput.FindInStructuredFile(AsNewFile("ci/rotate-tokens.yml", list)));
    }

    /// <summary>
    /// What reading the file is for: a secret on a line with nothing beside it, which the content rule
    /// cannot see because no name says it is one. A bracket, a list dash, quotes and a trailing comma
    /// are not something beside it. The place is the file's line, and the value is never named.
    /// </summary>
    /// <param name="content">The file's contents.</param>
    /// <param name="line">The line the value stands on.</param>
    [Theory]
    [InlineData("[\n  \"sk-Lq83Hd0PzX7vNm41RbTuKcWy\"\n]\n", 2)]
    [InlineData("[\n  \"first\",\n  \"9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c\",\n  \"last\"\n]\n", 3)]
    [InlineData("[\n  \"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r\"\n]\n", 2)]
    [InlineData("tokens:\n  - sk-Lq83Hd0PzX7vNm41RbTuKcWy\n", 2)]
    [InlineData("tokens:\n  - \"acme_aB3dE5gH7jK9mN1pQ3sT5vX7zB9dF1hJ3kL5\"\n", 2)]
    [InlineData("certificate: |\n  MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQC7\n", 2)]
    public void Withholds_a_secret_standing_alone_on_its_line(string content, int line) =>
        Assert.Equal($"line {line}", SensitiveInput.FindInStructuredFile(AsNewFile("config/tokens.json", content)));

    /// <summary>
    /// A diff sends more than its added lines, so the read does too: a removed line is read, its
    /// marker taken off first, and so is a list item, whose own dash follows the marker.
    /// </summary>
    [Fact]
    public void Reads_the_removed_and_added_lines_of_a_diff_past_their_markers()
    {
        var removed = """
                      diff --git a/config/tokens.json b/config/tokens.json
                      --- a/config/tokens.json
                      +++ b/config/tokens.json
                      @@ -1,3 +1,2 @@
                       [
                      -  "sk-Lq83Hd0PzX7vNm41RbTuKcWy",
                         "ordinary"
                       ]
                      """;
        var added = """
                    diff --git a/ci/rotate-tokens.yml b/ci/rotate-tokens.yml
                    --- a/ci/rotate-tokens.yml
                    +++ b/ci/rotate-tokens.yml
                    @@ -3,2 +3,3 @@
                       - first
                    +  - sk-Lq83Hd0PzX7vNm41RbTuKcWy
                       - last
                    """;

        Assert.Equal("line 2", SensitiveInput.FindInStructuredFile(removed));
        Assert.Equal("line 4", SensitiveInput.FindInStructuredFile(added));
    }

    /// <summary>The content rule is read with it: a secret beside a key that says so is withheld, and its field is named.</summary>
    [Fact]
    public void Withholds_what_the_content_rule_withholds_and_names_the_field()
    {
        var settings = "{\n  \"api_key\": \"sk-Lq83Hd0PzX7vNm41RbTuKcWy\"\n}\n";

        Assert.Equal("line 2, assigned to `api_key`", SensitiveInput.FindInStructuredFile(AsNewFile("config/app.json", settings)));
    }

    /// <summary>A file's contents as the new-file diff the review window holds for an untracked file.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="content">The file's contents.</param>
    private static string AsNewFile(string path, string content)
    {
        var lines = content.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        return $"diff --git a/{path} b/{path}\nnew file mode 100644\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1,{lines.Length} @@\n"
               + string.Join('\n', lines.Select(line => "+" + line));
    }

    [Fact]
    public void Refuses_a_prompt_carrying_a_secret()
    {
        var diff = """
                   +const config = {
                   +  api_key: "sk-Lq83Hd0PzX7vNm41RbTuKcWy",
                   +};
                   """;

        var error = Assert.Throws<SensitiveContentException>(() => SensitiveInput.Guard(diff, "the diff under review"));
        Assert.Contains("the diff under review", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Passes_a_prompt_that_only_mentions_secrets()
    {
        SensitiveInput.Guard("Add a test that the token endpoint rejects an expired password.", "the plan");
    }

    /// <summary>
    /// Issue #88. Every line here is ordinary code that assigns an expression to a name the keyword
    /// list recognises, and the guard called each one a secret: the value is 20-odd characters with
    /// no quote, space or comma in it, and a capital and a digit anywhere inside were the whole of
    /// the "three character classes" rule. The run that found it had the review refused over a
    /// <em>context</em> line of a file it had not touched, and the only way past was to rename a
    /// local in production code.
    /// </summary>
    [Theory]
    [InlineData("            var token = backtick.Groups[1].Value.Trim();")]
    [InlineData("    var token = context.Request.Headers2;")]
    [InlineData("        var tokenSource = CancellationTokenSource.CreateLinkedTokenSource2(ct);")]
    [InlineData("     private_key = ReadAllText(path).Trim2();")]
    public void Passes_code_that_assigns_an_expression_to_a_secret_sounding_name(string line) =>
        SensitiveInput.Guard(line, "the diff under review");

    /// <summary>
    /// The other half of the same change: what the guard is for. A literal has no call, no index
    /// and no dotted path of short identifiers, which is what tells it from an expression — and a
    /// JWT stays a secret because its dot-separated segments are far longer than an identifier is.
    /// </summary>
    [Theory]
    [InlineData("api_key = \"sk_live_4eC39HqLyjWDarjtT1zdp7dc\"")]
    [InlineData("+  api_key: \"sk-Lq83Hd0PzX7vNm41RbTuKcWy\",")]
    [InlineData("access_token=eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r")]
    [InlineData("SECRET=9f8e7d6c5b4a39281706f5e4d3c2b1a09f8e7d6c")]
    public void Still_refuses_a_real_secret(string line) =>
        Assert.Throws<SensitiveContentException>(() => SensitiveInput.Guard(line, "the diff under review"));

    /// <summary>
    /// A refusal that names nothing cannot be judged: the orchestrator cannot tell a real secret
    /// from a false positive, and the run that filed issue #88 had to replay the check by hand to
    /// find the line. The value itself is never named — that is the one thing being withheld.
    /// </summary>
    [Fact]
    public void Names_the_file_the_line_and_the_field_of_a_withheld_value()
    {
        var diff = """
                   diff --git a/src/Secrets.cs b/src/Secrets.cs
                   --- a/src/Secrets.cs
                   +++ b/src/Secrets.cs
                   @@ -40,6 +40,7 @@ public sealed class Secrets
                        public void Configure()
                        {
                   +        api_key = "sk-Lq83Hd0PzX7vNm41RbTuKcWy";
                        }
                   """;

        var error = Assert.Throws<SensitiveContentException>(() => SensitiveInput.Guard(diff, "the diff under review"));

        Assert.Contains("the diff under review", error.Message, StringComparison.Ordinal);
        Assert.Contains("src/Secrets.cs", error.Message, StringComparison.Ordinal);
        Assert.Contains("42", error.Message, StringComparison.Ordinal);
        Assert.Contains("api_key", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-Lq83", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Text that is not a diff still says where, counted in the text it was handed.</summary>
    [Fact]
    public void Names_the_line_when_the_text_is_not_a_diff()
    {
        var plan = "# Plan\n\nStep one.\napi_key = \"sk-Lq83Hd0PzX7vNm41RbTuKcWy\"\n";

        var error = Assert.Throws<SensitiveContentException>(() => SensitiveInput.Guard(plan, "the plan under review"));

        Assert.Contains("line 4", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("run/../../elsewhere")]
    public void Refuses_a_run_id_that_leaves_the_forge_folder(string runId)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "planforge-safety", Guid.NewGuid().ToString("n"));
        Assert.Throws<RunEscapedException>(() => RunDirectory.Open(workspace, runId));
    }

    /// <summary>
    /// A relative root would resolve against the server process, not the repository — and two
    /// sessions passing one would meet in the same folder.
    /// </summary>
    [Theory]
    [InlineData(".")]
    [InlineData("../other-repo")]
    [InlineData("relative/path")]
    public void Refuses_a_workspace_root_that_is_not_absolute(string workspaceRoot)
    {
        Assert.Throws<WorkspaceNotRootedException>(() => RunDirectory.Open(workspaceRoot, "any-run"));
        Assert.Throws<WorkspaceNotRootedException>(() => RunDirectory.Create(workspaceRoot, "any-run"));
    }

    /// <summary>
    /// Both contracts belong to both roles. Roslyn-first is how a critic reads C# and how a builder
    /// finds every place a fix has to reach: builders without it made no Roslyn call in 38 fix turns.
    /// The orchestration contract: a host hands whichever worker it runs whatever the user installed
    /// in it, and on 2026-08-29 that put this plugin's own skill and MCP server in front of a
    /// builder.
    /// </summary>
    [Fact]
    public void Appends_the_shared_contracts_each_role_is_owed()
    {
        var root = Path.Combine(Path.GetTempPath(), "planforge-prompts", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "claude"));
        File.WriteAllText(Path.Combine(root, "critic-contract.md"), "judge");
        File.WriteAllText(Path.Combine(root, "builder-contract.md"), "implement");
        File.WriteAllText(Path.Combine(root, "claude", "critic.md"), "answer through the tool");
        File.WriteAllText(Path.Combine(root, "claude", "builder.md"), "answer through the tool");
        File.WriteAllText(Path.Combine(root, "roslyn-contract.md"), "roslyn first");
        File.WriteAllText(Path.Combine(root, "orchestration-contract.md"), "the forge tools are not yours");

        try
        {
            var prompts = new PromptLibrary(root);
            var critic = prompts.Load("claude", VendorRole.Critic);
            var builder = prompts.Load("claude", VendorRole.Builder);

            Assert.Contains("roslyn first", critic, StringComparison.Ordinal);
            Assert.Contains("roslyn first", builder, StringComparison.Ordinal);
            Assert.Contains("the forge tools are not yours", critic, StringComparison.Ordinal);
            Assert.Contains("the forge tools are not yours", builder, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// One critic role, two review acts, and each contract belongs to exactly one of them: the
    /// requirements section a plan carries is not in front of a critic reading a diff, and "judge
    /// against the approved plan" has nothing to attach to while the plan is still a draft.
    /// </summary>
    [Fact]
    public void Appends_each_review_contract_to_its_own_act_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "planforge-prompts", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(root, "claude"));
        File.WriteAllText(Path.Combine(root, "critic-contract.md"), "judge");
        File.WriteAllText(Path.Combine(root, "claude", "critic.md"), "answer through the tool");
        File.WriteAllText(Path.Combine(root, "requirements-contract.md"), "the requirements are yours");
        File.WriteAllText(Path.Combine(root, "scope-contract.md"), "the scope is not yours");

        try
        {
            var prompts = new PromptLibrary(root);
            var planReview = prompts.LoadPlanReviewCritic("claude");
            var codeReview = prompts.LoadCodeReviewCritic("claude");

            Assert.Contains("the requirements are yours", planReview, StringComparison.Ordinal);
            Assert.DoesNotContain("the scope is not yours", planReview, StringComparison.Ordinal);
            Assert.Contains("the scope is not yours", codeReview, StringComparison.Ordinal);
            Assert.DoesNotContain("the requirements are yours", codeReview, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
