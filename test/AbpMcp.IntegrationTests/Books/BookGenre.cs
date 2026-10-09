namespace AbpMcp.IntegrationTests.Books;

/// <summary>
/// Fixture enum. Non-zero, non-positional values make the round-trip test meaningful:
/// an agent that sends the string name <c>"Fiction"</c> must bind to <see cref="Fiction"/>,
/// which only works because the dispatcher registers a string-enum converter. Without it,
/// System.Text.Json's Web default would demand the integer and reject the advertised schema.
/// </summary>
public enum BookGenre
{
    Unknown = 0,
    Fiction = 10,
    NonFiction = 20,
    Poetry = 30,
}
