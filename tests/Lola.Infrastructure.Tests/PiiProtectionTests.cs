using Dapper;
using Lola.Application.Security;
using Lola.Domain.Classification;
using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.Mapping;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// Acceptance bullet end-to-end: a `[PiiClass]`-annotated row written
/// through the protection boundary stores ciphertext + a blind index;
/// the index still finds the row; reading through the boundary returns
/// plaintext — repositories never handle it.
[Collection("openbao")]
public sealed class PiiProtectionTests(OpenBaoFixture fixture)
{
    private sealed class Borrower
    {
        public int Id { get; set; }
        public string? Name { get; set; }

        [PiiClass(PiiClass.Ssn, Searchable = true)]
        public string? Ssn { get; set; }
    }

    [Fact]
    public async Task PiiRow_StoresCiphertext_BlindIndexFindsIt_ReadReturnsPlaintext()
    {
        var tenant = fixture.NewTenant();
        using var _ = CipherContext.Use(tenant);
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(tenant);
        try
        {
            var cipher = new TransitFieldCipher(fixture.Transit);
            var index = new TransitBlindIndex(fixture.Transit);
            var db = Path.Combine(Path.GetTempPath(), $"lola-test-{Guid.NewGuid():N}.db");
            try
            {
                await using var conn = new SqliteConnection($"Data Source={db};Pooling=false");
                await conn.OpenAsync();
                await conn.ExecuteAsync(
                    "CREATE TABLE borrowers (id INTEGER PRIMARY KEY, name TEXT, ssn TEXT, ssn_idx TEXT)");

                var row = new Borrower { Id = 1, Name = "Test Borrower", Ssn = "123-45-6789" };
                var p = await PiiProtection.ToParametersAsync(row, cipher, index);
                await conn.ExecuteAsync(
                    "INSERT INTO borrowers (id, name, ssn, ssn_idx) VALUES (@Id, @Name, @Ssn, @SsnIdx)", p);

                // Stored form: ciphertext + versioned index — no plaintext.
                var raw = await conn.QuerySingleAsync<(string ssn, string idx)>(
                    "SELECT ssn, ssn_idx FROM borrowers WHERE id = 1");
                raw.ssn.ShouldStartWith("vault:v1:");
                raw.ssn.ShouldNotContain("123-45-6789");
                raw.idx.ShouldStartWith("idx:v1:");

                // Lookup normalizes: differently-formatted SSN finds the row.
                var lookup = await index.IndexAsync(PiiClass.Ssn, "123456789");
                (await conn.QuerySingleAsync<int>(
                    "SELECT COUNT(*) FROM borrowers WHERE ssn_idx = @lookup", new { lookup }))
                    .ShouldBe(1);

                // Read boundary decrypts transparently.
                var back = (await conn.QueryPiiAsync<Borrower>(
                    "SELECT id, name, ssn FROM borrowers WHERE id = 1", null, cipher)).Single();
                back.Ssn.ShouldBe("123-45-6789");
                back.Name.ShouldBe("Test Borrower");
            }
            finally { File.Delete(db); }
        }
        finally { await km.DestroyAsync(tenant); }
    }

    [Fact]
    public async Task NullPii_ProducesNullParams()
    {
        var tenant = fixture.NewTenant();
        using var _ = CipherContext.Use(tenant);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var index = new TransitBlindIndex(fixture.Transit);

        var p = await PiiProtection.ToParametersAsync(
            new Borrower { Id = 2, Name = "No Ssn" }, cipher, index);
        p.Get<string?>("Ssn").ShouldBeNull();
        p.Get<string?>("SsnIdx").ShouldBeNull();
        p.Get<string?>("Name").ShouldBe("No Ssn");
    }
}
