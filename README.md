# Minden Game Notes Builder V1

A small Windows/WPF application for building a consistent eight-page Minden High School football game-notes packet.

## Included

- Weekly matchup and editorial editor
- Known-column `.xlsx` statistics import (no Excel installation required)
- `.pdf` statistics import through Poppler `pdftotext`
- Persistent normalized project data in `%LOCALAPPDATA%\MindenGameNotes\project.json`
- Explicit verification state for imported player rows and an export warning
- Eight-page live preview
- Dependency-free, vector/text, US Letter PDF export

## Run

```powershell
dotnet run --project src/MindenGameNotes
```

## Excel mapping

The first worksheet must have a header row. Recognized aliases include `Player`/`Name`, `No`/`Number`/`Jersey`, `Pos`/`Position`, `GP`/`Games`, `Pass Yds`, `Rush Yds`, `Rec Yds`, `Tackles`/`TKL`, and `Touchdowns`/`TD`. Imports upsert players by name and never mark data verified automatically.

That legacy generic import is not defensive authority. Jake's defensive workbook uses the separate **Defensive intake** workflow: every game worksheet and the source `TOTALS` worksheet is staged independently, reviewed, and explicitly accepted or replaced. Accepted defensive information is retained with immutable import provenance and is exposed through `AcceptedDefensiveInformationSupply`; it is not sent to publication layout by WP 3.

## Locked design

`PageComposer` is the single eight-page layout definition used by both preview and PDF output. The initial implementation provides the locked page count, page order, school palette, typography hierarchy, headers, and footers. Production artwork/fonts can be incorporated there once the approved design assets are supplied.
