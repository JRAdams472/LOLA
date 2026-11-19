using Lola.Application.Audit;
using Lola.Application.Events;
using Lola.Application.Security;
using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Application.Tests;

/// M15 P1 — `loan_events` hash-chain mechanics: canonicalization is
/// stable, a tampered row's stored hash fails recompute, and a broken
/// prev link reports the right seq.
public sealed class EventChainTests
{
    private static LoanEvent Row(long seq, string eventId, string prev,
        string actor = "officer-1", string after = "1000",
        string? fieldClass = null)
    {
        var created = "2026-11-19T00:00:00.000Z";
        var hash = EventChain.ComputeHash(prev, eventId, "app-1",
            "field_change", actor, "application", "app-1",
            "loan.amount", fieldClass, "900", after, created);
        return new LoanEvent(seq, eventId, "app-1", "field_change",
            actor, "application", "app-1", "loan.amount", fieldClass,
            "900", after, prev, hash, created);
    }

    [Fact]
    public void Clean_chain_verifies()
    {
        var e1 = Row(1, "lev-1", HashChain.Genesis);
        var e2 = Row(2, "lev-2", e1.Hash);
        var e3 = Row(3, "lev-3", e2.Hash);
        EventChain.FirstBroken([e1, e2, e3]).ShouldBeNull();
    }

    [Fact]
    public void Tampered_row_reports_its_seq()
    {
        var e1 = Row(1, "lev-1", HashChain.Genesis);
        var e2 = Row(2, "lev-2", e1.Hash);
        var forged = e2 with { AfterVal = "9999" }; // stored hash now stale
        var e3 = Row(3, "lev-3", e2.Hash);
        EventChain.FirstBroken([e1, forged, e3]).ShouldBe(2);
    }

    [Fact]
    public void Deleted_row_breaks_the_next_link()
    {
        var e1 = Row(1, "lev-1", HashChain.Genesis);
        var e2 = Row(2, "lev-2", e1.Hash);
        var e3 = Row(3, "lev-3", e2.Hash);
        EventChain.FirstBroken([e1, e3]).ShouldBe(3);
    }

    [Fact]
    public void Hash_covers_mutation_fields()
    {
        // Same spine, different after_val → different hash.
        var a = Row(1, "lev-1", HashChain.Genesis, after: "1");
        var b = Row(1, "lev-1", HashChain.Genesis, after: "2");
        a.Hash.ShouldNotBe(b.Hash);
    }
}

/// M15 P1 — `LoanEventEncoding`: PII-classed values go through the
/// tenant blind index (HMAC), everything else stays plaintext.
public sealed class LoanEventEncodingTests
{
    private sealed class FakeIndex : IBlindIndex
    {
        public int Calls;
        public Task<string> IndexAsync(PiiClass classification,
            string value, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult($"idx:v1:{classification}:{value.GetHashCode():x8}");
        }
    }

    [Fact]
    public async Task Pii_field_values_are_hmac_tokens()
    {
        var idx = new FakeIndex();
        var draft = new LoanEventDraft("app-1", "field_change",
            "officer-1", "application", "app-1", "party[0].ssn",
            PiiClass.Ssn, "111-22-3333", "444-55-6666");
        var enc = await LoanEventEncoding.EncodeAsync(draft, idx);
        idx.Calls.ShouldBe(2);
        enc.BeforeVal.ShouldStartWith("idx:v1:");
        enc.AfterVal.ShouldStartWith("idx:v1:");
        enc.BeforeVal.ShouldNotContain("111-22-3333");
        enc.AfterVal.ShouldNotContain("444-55-6666");
    }

    [Fact]
    public async Task Non_pii_values_pass_through()
    {
        var idx = new FakeIndex();
        var draft = new LoanEventDraft("app-1", "field_change",
            "officer-1", "application", "app-1", "loan.amount",
            null, "900", "1000");
        var enc = await LoanEventEncoding.EncodeAsync(draft, idx);
        idx.Calls.ShouldBe(0);
        enc.BeforeVal.ShouldBe("900");
        enc.AfterVal.ShouldBe("1000");
    }

    [Fact]
    public async Task Null_value_is_not_indexed()
    {
        var idx = new FakeIndex();
        var draft = new LoanEventDraft("app-1", "field_change",
            "officer-1", "application", "app-1", "party[0].ssn",
            PiiClass.Ssn, null, "444-55-6666");
        var enc = await LoanEventEncoding.EncodeAsync(draft, idx);
        idx.Calls.ShouldBe(1);
        enc.BeforeVal.ShouldBeNull();
    }
}
