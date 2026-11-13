namespace Linkbase.Data;

// Stored as snake_case strings (see SnakeCaseEnumConverter): GradeSystem.UkTrad is "uk_trad".

public enum AreaKind { Country, Region, Area }

public enum GradeSystem
{
    UkTrad, UkTech, French, Yds, Font, V, Ewbank, Uiaa, Australian, Nordic, Alpine, Scrambling, ViaFerrata, Ice, Other,
}

public enum Discipline { Trad, Sport, Boulder, Winter, Alpine, Other }

public enum AscentStyle { Lead, Onsight, Flash, Redpoint, Send, Hangdog, Second, TopRope, Solo, Dnf, Other }

public enum DatePrecision { Day, Month, Year }

public enum DataSource { Manual, Ukc, MountainProject, WebImport, Csv, Ai }


public enum ImportStatus { Pending, Staged, Committed, Undone, Failed }


// What a saved view reopens: the Explore page, or one type's record list.
public enum SavedViewKind { Explore, List }
