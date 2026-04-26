using AbpMcp.Sample.Library;
using Microsoft.EntityFrameworkCore;

namespace AbpMcp.Sample;

/// <summary>
/// Pre-populates the in-memory library with classic titles across several genres
/// and one demo member, so the quickstart and the launch demo video have something
/// real to interact with on first request.
/// </summary>
internal static class LibrarySeedData
{
    public static async Task EnsureSeededAsync(LibraryDbContext db)
    {
        if (await db.Titles.AnyAsync().ConfigureAwait(false))
        {
            return; // already seeded
        }

        var titles = new[]
        {
            CreateTitle("The Hobbit", "J.R.R. Tolkien", 1937, "Fantasy",
                "Bilbo Baggins is hired as a burglar by a company of dwarves on a quest for treasure.",
                ed("Hardcover", "Allen & Unwin", 1937, "978-0007458424", 3),
                ed("Paperback", "Houghton Mifflin", 2002, "978-0547928227", 5),
                ed("Ebook", "HarperCollins", 2010, "978-0007487318", 99),
                ed("Audiobook", "Recorded Books", 2012, "978-1470831936", 2)),

            CreateTitle("Dune", "Frank Herbert", 1965, "Science Fiction",
                "On the desert planet Arrakis, young Paul Atreides becomes entangled in a war for the galaxy's most valuable resource.",
                ed("Hardcover", "Chilton Books", 1965, "978-0801950773", 1),
                ed("Paperback", "Ace Books", 1990, "978-0441172719", 4),
                ed("Ebook", "Ace Books", 2010, "978-0441013593", 99)),

            CreateTitle("Pride and Prejudice", "Jane Austen", 1813, "Romance",
                "Elizabeth Bennet navigates issues of manners, upbringing, morality, and marriage in 19th-century England.",
                ed("Paperback", "Penguin Classics", 2002, "978-0141439518", 6),
                ed("Ebook", "Project Gutenberg", 1998, "978-1503290563", 99)),

            CreateTitle("The Great Gatsby", "F. Scott Fitzgerald", 1925, "Literary Fiction",
                "Jay Gatsby's obsessive love for Daisy Buchanan plays out against the glittering excess of 1920s Long Island.",
                ed("Hardcover", "Scribner", 1925, "978-0743273565", 2),
                ed("Audiobook", "Recorded Books", 2002, "978-1402577314", 1)),

            CreateTitle("1984", "George Orwell", 1949, "Dystopian",
                "Winston Smith works at the Ministry of Truth in a totalitarian state where the Party watches everything.",
                ed("Paperback", "Secker & Warburg", 1949, "978-0451524935", 5),
                ed("Ebook", "Penguin", 2003, "978-0452284234", 99),
                ed("Audiobook", "Audible Studios", 2014, "978-1452652023", 3)),

            CreateTitle("Foundation", "Isaac Asimov", 1951, "Science Fiction",
                "Hari Seldon develops psychohistory to predict the fall of the Galactic Empire and shorten the dark age that follows.",
                ed("Paperback", "Bantam Spectra", 1991, "978-0553293357", 3),
                ed("Ebook", "Spectra", 2008, "978-0553900347", 99)),
        };

        var demoMember = new Member
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Demo Member",
            Email = "demo@example.com",
            MemberSince = DateTime.UtcNow.AddYears(-1),
            Status = MemberStatus.Active,
        };

        db.Titles.AddRange(titles);
        db.Members.Add(demoMember);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static Title CreateTitle(
        string name,
        string author,
        int firstPublishedYear,
        string genre,
        string synopsis,
        params Edition[] editions)
    {
        var title = new Title
        {
            Id = Guid.NewGuid(),
            Name = name,
            Author = author,
            FirstPublishedYear = firstPublishedYear,
            Genre = genre,
            Synopsis = synopsis,
            Editions = editions.ToList(),
        };
        foreach (var e in editions)
        {
            e.Id = Guid.NewGuid();
        }
        return title;
    }

    private static Edition ed(string format, string publisher, int releaseYear, string isbn, int copies) => new()
    {
        Format = Enum.Parse<Format>(format),
        Publisher = publisher,
        ReleaseYear = releaseYear,
        Isbn = isbn,
        TotalCopies = copies,
    };
}
