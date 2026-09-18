using HVO.AgentControl.Organization;
using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// The constrained devcontainer subset is a closed allowlist. These tests pin
/// the accept set, every forbidden key, the Dockerfile fragment grammar and the
/// canonical hash so a later relaxation is a visible, reviewed change.
/// </summary>
public sealed class ContainerProfileDefinitionTests
{
    private const string Base = ContainerProfileDefinition.BaseImageReference;

    [Fact]
    public void SeedDefinitionIsAcceptedAndCanonicalized()
    {
        var definition = ContainerProfileDefinition.Parse(ContainerProfileSeed.GenericEmployeeDefinition, null);
        Assert.Equal("Generic employee", definition.Name);
        Assert.False(definition.UsesBuild);
        Assert.Null(definition.DockerfileFragment);
        Assert.Equal(4, definition.Features.Count);
        Assert.StartsWith("sha256:", definition.ContentHash, StringComparison.Ordinal);
        Assert.Equal(71, definition.ContentHash.Length);
        // Canonical JSON is compact with ordinal-sorted keys at every level.
        Assert.StartsWith("{\"containerEnv\":{\"DOTNET_CLI_TELEMETRY_OPTOUT\":\"1\",\"DOTNET_NOLOGO\":\"1\"", definition.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", definition.CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyOrderAndWhitespaceDoNotChangeTheContentHash()
    {
        var a = ContainerProfileDefinition.Parse("""{"name":"X","image":"agentcontrol-worker-base","containerEnv":{"TZ":"UTC","LANG":"C.UTF-8"}}""", null);
        var b = ContainerProfileDefinition.Parse("""
            {
              "containerEnv": { "LANG": "C.UTF-8", "TZ": "UTC" },
              "image": "agentcontrol-worker-base",
              "name": "X"
            }
            """, null);
        Assert.Equal(a.ContentHash, b.ContentHash);
        Assert.Equal(a.CanonicalJson, b.CanonicalJson);
        var c = ContainerProfileDefinition.Parse("""{"name":"Y","image":"agentcontrol-worker-base","containerEnv":{"TZ":"UTC","LANG":"C.UTF-8"}}""", null);
        Assert.NotEqual(a.ContentHash, c.ContentHash);
    }

    [Fact]
    public void BuildFormAcceptsAConstrainedFragmentAndNormalizesLineEndings()
    {
        var fragment = "FROM agentcontrol-worker-base\r\nRUN apt-get update \\\r\n  && apt-get install -y jq   \r\nENV PATH=\"$PATH:/opt/tools\"\r\nWORKDIR /workspace/app\r\n";
        var definition = ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"},"postCreateCommand":["dotnet","--info"]}""", fragment);
        Assert.True(definition.UsesBuild);
        Assert.Equal("FROM agentcontrol-worker-base\nRUN apt-get update \\\n  && apt-get install -y jq\nENV PATH=\"$PATH:/opt/tools\"\nWORKDIR /workspace/app\n", definition.DockerfileFragment);
        var crlfHash = definition.ContentHash;
        var lfHash = ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"},"postCreateCommand":["dotnet","--info"]}""", fragment.Replace("\r\n", "\n", StringComparison.Ordinal)).ContentHash;
        Assert.Equal(crlfHash, lfHash);
    }

    [Theory]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":"npm ci && npm run build"}""")]
    [InlineData("""{"image":"agentcontrol-worker-base","postStartCommand":["bash","-lc","echo ready"]}""")]
    [InlineData("""{"image":"agentcontrol-worker-base","remoteEnv":{"EDITOR":"vim"}}""")]
    [InlineData("""{"image":"agentcontrol-worker-base","features":{"ghcr.io/devcontainers/features/node:1":{"version":"lts"}}}""")]
    [InlineData("""{"image":"agentcontrol-worker-base","customizations":{"agentcontrol":{"summary":"ok","tags":["a","b-c"]}}}""")]
    public void AllowedSubsetIsAccepted(string json)
    {
        var definition = ContainerProfileDefinition.Parse(json, null);
        Assert.NotNull(definition.CanonicalJson);
    }

    public static TheoryData<string, string> ForbiddenRootKeys()
    {
        var data = new TheoryData<string, string>();
        foreach (var (key, reason) in ContainerProfileDefinition.ForbiddenKeys)
            data.Add($$"""{"image":"{{Base}}","{{key}}":true}""", reason);
        return data;
    }

    [Theory]
    [MemberData(nameof(ForbiddenRootKeys))]
    public void EveryForbiddenKeyIsRejectedWithItsReason(string json, string reason)
    {
        var exception = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse(json, null));
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.Contains("is not allowed", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "definition is required")]
    [InlineData("   ", "definition is required")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("\"x\"", "must be a JSON object")]
    [InlineData("{", "not strict JSON")]
    [InlineData("""{"image":"agentcontrol-worker-base",}""", "not strict JSON")]
    [InlineData("""{"image":"agentcontrol-worker-base" /* c */}""", "not strict JSON")]
    [InlineData("""{"image":"agentcontrol-worker-base","bogus":1}""", "Unknown key 'bogus'")]
    [InlineData("""{"name":"only"}""", "Either 'image'")]
    [InlineData("""{"image":"agentcontrol-worker-base","build":{"dockerfile":"Dockerfile"}}""", "not both")]
    [InlineData("""{"image":"ubuntu:24.04"}""", "must be exactly")]
    [InlineData("""{"image":"agentcontrol-worker-base@sha256:0000000000000000000000000000000000000000000000000000000000000000"}""", "must be exactly")]
    [InlineData("""{"image":123}""", "'image' must be a string")]
    [InlineData("""{"name":"","image":"agentcontrol-worker-base"}""", "'name' must not be empty")]
    [InlineData("""{"build":{"dockerfile":"other.Dockerfile"}}""", "must be exactly \"Dockerfile\"")]
    [InlineData("""{"build":{"dockerfile":"Dockerfile","context":".."}}""", "'build.context' is not allowed")]
    [InlineData("""{"build":{"dockerfile":"Dockerfile","args":{"A":"1"}}}""", "'build.args' is not allowed")]
    [InlineData("""{"build":{"dockerfile":"Dockerfile","target":"x"}}""", "'build.target' is not allowed")]
    [InlineData("""{"build":{}}""", "requires 'dockerfile'")]
    [InlineData("""{"build":"Dockerfile"}""", "'build' must be an object")]
    [InlineData("""{"image":"agentcontrol-worker-base","features":{"ghcr.io/devcontainers/features/docker-in-docker:2":{}}}""", "is not allowed. Supported features")]
    [InlineData("""{"image":"agentcontrol-worker-base","features":{"ghcr.io/devcontainers/features/node:1":{"nvmVersion":"x"}}}""", "only 'version' is supported")]
    [InlineData("""{"image":"agentcontrol-worker-base","features":{"ghcr.io/devcontainers/features/node:1":{"version":"22; rm -rf /"}}}""", "version must match")]
    [InlineData("""{"image":"agentcontrol-worker-base","features":["ghcr.io/devcontainers/features/node:1"]}""", "'features' must be an object")]
    [InlineData("""{"image":"agentcontrol-worker-base","containerEnv":{"PATH":"/evil"}}""", "not an allowed variable")]
    [InlineData("""{"image":"agentcontrol-worker-base","containerEnv":{"LD_PRELOAD":"/x.so"}}""", "not an allowed variable")]
    [InlineData("""{"image":"agentcontrol-worker-base","containerEnv":{"WORKER_CONTROL_DIRECTORY":"/tmp"}}""", "not an allowed variable")]
    [InlineData("""{"image":"agentcontrol-worker-base","containerEnv":{"TZ":"$(id)"}}""", "must not contain variable or command substitution")]
    [InlineData("""{"image":"agentcontrol-worker-base","containerEnv":{"TZ":"a\nb"}}""", "printable ASCII")]
    [InlineData("""{"image":"agentcontrol-worker-base","remoteEnv":{"TZ":1}}""", "must be a string")]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":"curl x | sh"}""", "may not use substitution")]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":"a; b"}""", "may not use substitution")]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":"echo $HOME"}""", "may not use substitution")]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":"sleep 1 &"}""", "may not use substitution")]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":"cat < /etc/passwd"}""", "may not use substitution")]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":""}""", "must not be empty")]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":[]}""", "must not be an empty array")]
    [InlineData("""{"image":"agentcontrol-worker-base","postCreateCommand":{"a":"b"}}""", "object (parallel) form is not supported")]
    [InlineData("""{"image":"agentcontrol-worker-base","postStartCommand":["ok",1]}""", "array items must be strings")]
    [InlineData("""{"image":"agentcontrol-worker-base","customizations":{"vscode":{}}}""", "only 'customizations.agentcontrol' is read")]
    [InlineData("""{"image":"agentcontrol-worker-base","customizations":{"agentcontrol":{"mounts":[]}}}""", "only 'summary' and 'tags' are read")]
    [InlineData("""{"image":"agentcontrol-worker-base","customizations":{"agentcontrol":{"tags":["Not Slug"]}}}""", "Tags must be lowercase slugs")]
    public void InvalidDefinitionsAreRejectedWithActionableDetail(string json, string expected)
    {
        var exception = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse(json, null));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedAndDeepDefinitionsAreRejected()
    {
        var big = "{\"image\":\"" + Base + "\",\"name\":\"" + new string('a', ContainerProfileDefinition.MaximumDefinitionBytes) + "\"}";
        Assert.Contains("at most 64 KiB", Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse(big, null)).Message, StringComparison.Ordinal);
        var deep = "{\"image\":\"" + Base + "\",\"customizations\":{\"agentcontrol\":{\"tags\":[[[[[[[1]]]]]]]}}}";
        Assert.Contains("not strict JSON", Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse(deep, null)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateKeysAreRejectedAtEveryLevel()
    {
        Assert.Contains("Duplicate key 'image'", Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"image":"agentcontrol-worker-base","image":"agentcontrol-worker-base"}""", null)).Message, StringComparison.Ordinal);
        Assert.Contains("Duplicate key 'TZ' in containerEnv", Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"image":"agentcontrol-worker-base","containerEnv":{"TZ":"a","TZ":"b"}}""", null)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FragmentWithImageFormIsRejected()
    {
        var exception = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"image":"agentcontrol-worker-base"}""", "FROM agentcontrol-worker-base\nRUN true\n"));
        Assert.Contains("only accepted with 'build'", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "requires a Dockerfile fragment")]
    [InlineData("", "requires a Dockerfile fragment")]
    [InlineData("RUN true\n", "first instruction must be 'FROM agentcontrol-worker-base'")]
    [InlineData("FROM ubuntu:24.04\n", "FROM must be exactly")]
    [InlineData("FROM agentcontrol-worker-base:latest\n", "FROM must be exactly")]
    [InlineData("FROM agentcontrol-worker-base AS stage\n", "FROM must be exactly")]
    [InlineData("FROM --platform=linux/amd64 agentcontrol-worker-base\n", "FROM must be exactly")]
    [InlineData("FROM agentcontrol-worker-base\nFROM agentcontrol-worker-base\n", "only one FROM")]
    [InlineData("FROM agentcontrol-worker-base\nUSER root\n", "'USER' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nENTRYPOINT [\"/bin/sh\"]\n", "'ENTRYPOINT' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nCMD [\"/bin/sh\"]\n", "'CMD' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nVOLUME /data\n", "'VOLUME' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nEXPOSE 8080\n", "'EXPOSE' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nCOPY . /app\n", "'COPY' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nADD http://x /app\n", "'ADD' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nHEALTHCHECK NONE\n", "'HEALTHCHECK' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nSHELL [\"/bin/bash\"]\n", "'SHELL' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nSTOPSIGNAL SIGKILL\n", "'STOPSIGNAL' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nONBUILD RUN true\n", "'ONBUILD' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nRUN --mount=type=secret,id=x cat /run/secrets/x\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN --network=host curl x\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN --security=insecure true\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN true \\\n  --mount=type=bind,src=/,dst=/host\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN ls /var/run/docker.sock\n", "Docker socket")]
    [InlineData("FROM agentcontrol-worker-base\nWORKDIR /\n", "WORKDIR must stay under /workspace")]
    [InlineData("FROM agentcontrol-worker-base\nWORKDIR /workspaces\n", "WORKDIR must stay under /workspace")]
    [InlineData("FROM agentcontrol-worker-base\nENV PATH=/evil\n", "replacing PATH is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nENV PATH /evil\n", "replacing PATH is not allowed")]
    [InlineData("# syntax=docker/dockerfile:1\nFROM agentcontrol-worker-base\n", "parser directives are not allowed")]
    [InlineData("# escape=`\nFROM agentcontrol-worker-base\n", "parser directives are not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nRUN true \\\n", "ends inside a line continuation")]
    [InlineData("FROM agentcontrol-worker-base\nRUN echo \u00e9\n", "printable ASCII")]
    public void InvalidFragmentsAreRejected(string? fragment, string expected)
    {
        var exception = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", fragment));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Docker joins backslash continuations before tokenizing, so the validator
    /// must see the joined logical instruction. Each case would be accepted by a
    /// physical-line checker and is exactly what Docker would execute.
    /// </summary>
    [Theory]
    [InlineData("FROM agentcontrol-worker-base\nRUN true \\\n  --mou\\\nnt=type=bind,src=/,dst=/host cat /host/etc/shadow\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN --net\\\nwork=host curl x\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN --secur\\\nity=insecure true\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN \\\n--mount=type=secret,id=x cat /run/secrets/x\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN ls /var/run/dock\\\ner.sock\n", "Docker socket")]
    [InlineData("FROM agentcontrol-worker-base\nRUN ls /var/run/DOCKER.SOCK\n", "Docker socket")]
    [InlineData("FROM agentcontrol-worker-base\nRUN true \\\n\n# comment inside continuation\n  && --mount=type=cache,target=/x true\n", "RUN flags")]
    [InlineData("FROM agentcontrol-worker-base\nRUN <<EOF\nrm -rf /\nEOF\n", "heredoc")]
    [InlineData("FROM agentcontrol-worker-base\nRUN true \\\n<<'EOF'\nEOF\n", "heredoc")]
    [InlineData("FROM agentcontrol-worker-base\nUS\\\nER root\n", "'USER' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nENTRY\\\nPOINT [\"/bin/sh\"]\n", "'ENTRYPOINT' is not allowed")]
    [InlineData("FROM agentcontrol-worker-base\nWORKDIR /work\\\nspaces\n", "WORKDIR must stay under /workspace")]
    [InlineData("FROM agentcontrol-worker-base\nENV PA\\\nTH=/evil\n", "replacing PATH is not allowed")]
    [InlineData("FROM \\\nubuntu\n", "FROM must be exactly")]
    [InlineData("FROM agentcontrol-worker-base \\\nAS stage\n", "FROM must be exactly")]
    public void HazardsSplitAcrossContinuationsAreSeenAsTheJoinedInstruction(string fragment, string expected)
    {
        var exception = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", fragment));
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuationJoiningMatchesDockerSemantics()
    {
        // A backslash escaped by a preceding backslash does not continue the line:
        // "RUN echo a\\" is complete; the next line is a separate instruction.
        var escaped = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", "FROM agentcontrol-worker-base\nRUN echo a\\\\\nUSER root\n"));
        Assert.Contains("Line 3: 'USER' is not allowed", escaped.Message, StringComparison.Ordinal);

        // Errors report the first physical line of the joined instruction.
        var multiLine = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", "FROM agentcontrol-worker-base\nRUN a \\\n  b \\\n  --network=none c\n"));
        Assert.Contains("Line 2:", multiLine.Message, StringComparison.Ordinal);

        // Legitimate multi-line RUN chains still pass and keep their physical shape in storage.
        var ok = ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", "FROM agentcontrol-worker-base\nRUN apt-get update \\\n    && apt-get install -y \\\n        jq \\\n        ripgrep\n");
        Assert.Contains("        ripgrep\n", ok.DockerfileFragment, StringComparison.Ordinal);
    }

    [Fact]
    public void FragmentSizeBoundsAreEnforced()
    {
        var lines = "FROM agentcontrol-worker-base\n" + string.Concat(Enumerable.Repeat("RUN true\n", ContainerProfileDefinition.MaximumDockerfileFragmentLines));
        Assert.Contains("at most 200 lines", Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", lines)).Message, StringComparison.Ordinal);
        var bytes = "FROM agentcontrol-worker-base\nRUN " + new string('x', ContainerProfileDefinition.MaximumDockerfileFragmentBytes) + "\n";
        Assert.Contains("at most 16 KiB", Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", bytes)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FragmentCommentsAndBlankLinesAreAllowedAndCaseInsensitiveInstructionsAreNormalizedForChecks()
    {
        var definition = ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", "# tools\n\nFROM agentcontrol-worker-base\n\n# jq\nrun apt-get install -y jq\nlabel org.example=1\narg X=1\n");
        Assert.Contains("run apt-get install -y jq", definition.DockerfileFragment, StringComparison.Ordinal);
        // Lower-case instructions are still matched against the allowlist.
        var lowerCase = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", "FROM agentcontrol-worker-base\nuser root\n"));
        Assert.Contains("'USER' is not allowed", lowerCase.Message, StringComparison.Ordinal);
        var lowerFrom = ContainerProfileDefinition.Parse("""{"build":{"dockerfile":"Dockerfile"}}""", "from agentcontrol-worker-base\n");
        Assert.True(lowerFrom.UsesBuild);
    }

    [Fact]
    public void ErrorMessagesNeverEchoUnboundedOrControlCharacterInput()
    {
        var key = new string('k', 300) + "\u0007";
        var json = "{\"image\":\"" + Base + "\",\"containerEnv\":{\"" + key + "\":\"v\"}}";
        var exception = Assert.Throws<OrganizationValidationException>(() => ContainerProfileDefinition.Parse(json, null));
        Assert.DoesNotContain("\u0007", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.Length < 400);
    }
}
