$CSPROJ="typebeat.Game/typebeat.Game.csproj"
$SLN="typebeat.sln"

dotnet remove $CSPROJ package ppy.osu.Game.Resources;
dotnet sln $SLN add ../osu-resources/osu.Game.Resources/osu.Game.Resources.csproj
dotnet add $CSPROJ reference ../osu-resources/osu.Game.Resources/osu.Game.Resources.csproj

$SLNF=Get-Content "typebeat.Desktop.slnf" | ConvertFrom-Json
$TMP=New-TemporaryFile
$SLNF.solution.projects += ("../osu-resources/osu.Game.Resources/osu.Game.Resources.csproj")
ConvertTo-Json $SLNF | Out-File $TMP -Encoding UTF8
Move-Item -Path $TMP -Destination "typebeat.Desktop.slnf" -Force
