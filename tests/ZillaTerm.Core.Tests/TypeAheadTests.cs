namespace ZillaTerm.Core.Tests;

public class TypeAheadTests
{
    private static readonly string[] Names = ["..", "app", "Backup", "bin", "boot.log", "data", "rapport final.txt", "srv01.conf", "srv02.conf"];
    private static readonly DateTime T0 = new(2026, 10, 12, 9, 0, 0);

    [Fact]
    public void FirstLetter_SelectsTheNextNameStartingWithIt()
    {
        var search = new TypeAhead();

        Assert.Equal(2, search.Find(Names, -1, "b", T0)); // sans tenir compte de la casse
        Assert.Equal(5, search.Find(Names, 0, "d", T0.AddSeconds(2)));
        // Après une pause, à partir de l'élément qui suit la sélection, en revenant au début.
        Assert.Equal(1, search.Find(Names, 5, "a", T0.AddSeconds(4)));
        Assert.Equal(-1, search.Find(Names, 1, "x", T0.AddSeconds(6)));
    }

    [Fact]
    public void LettersTypedQuickly_FormTheStartOfTheName()
    {
        var search = new TypeAhead();

        Assert.Equal(2, search.Find(Names, -1, "b", T0));
        Assert.Equal(3, search.Find(Names, 2, "i", T0.AddMilliseconds(300)));
        Assert.Equal(7, search.Find(Names, 3, "s", T0.AddSeconds(3)));
        Assert.Equal(7, search.Find(Names, 7, "r", T0.AddSeconds(3.3)));
        Assert.Equal(7, search.Find(Names, 7, "v", T0.AddSeconds(3.6)));
        Assert.Equal(7, search.Find(Names, 7, "0", T0.AddSeconds(3.9)));
        Assert.Equal(8, search.Find(Names, 7, "2", T0.AddSeconds(4.2)));
    }

    [Fact]
    public void SameLetterAgain_GoesToTheNextNameStartingWithIt()
    {
        var search = new TypeAhead();

        Assert.Equal(2, search.Find(Names, -1, "b", T0));
        Assert.Equal(3, search.Find(Names, 2, "b", T0.AddMilliseconds(200)));
        Assert.Equal(4, search.Find(Names, 3, "b", T0.AddMilliseconds(400)));
        Assert.Equal(2, search.Find(Names, 4, "B", T0.AddMilliseconds(600)));
        // Lentement aussi : la lettre suivante part de la sélection.
        Assert.Equal(3, search.Find(Names, 2, "b", T0.AddSeconds(5)));
    }

    [Fact]
    public void Space_ContinuesAName_ButNeverStartsASearch()
    {
        var search = new TypeAhead();

        Assert.Equal(-1, search.Find(Names, 1, " ", T0));
        Assert.Equal(6, search.Find(Names, 1, "rapport", T0.AddSeconds(2)));
        Assert.Equal(6, search.Find(Names, 6, " ", T0.AddSeconds(2.2)));
        Assert.Equal(6, search.Find(Names, 6, "f", T0.AddSeconds(2.4)));
        Assert.Equal(-1, search.Find(Names, 6, "x", T0.AddSeconds(2.6)));
    }

    [Fact]
    public void EmptyList_SelectsNothing() =>
        Assert.Equal(-1, new TypeAhead().Find([], -1, "a", T0));
}
