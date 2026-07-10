$CSPROJ="typebeat.Game/typebeat.Game.csproj"
$SLN="osu.sln"

dotnet remove $CSPROJ package ppy.typebeat.Game.Resources;
dotnet sln $SLN add ../osu-resources/typebeat.Game.Resources/typebeat.Game.Resources.csproj
dotnet add $CSPROJ reference ../osu-resources/typebeat.Game.Resources/typebeat.Game.Resources.csproj

$SLNF=Get-Content "typebeat.Desktop.slnf" | ConvertFrom-Json
$TMP=New-TemporaryFile
$SLNF.solution.projects += ("../osu-resources/typebeat.Game.Resources/typebeat.Game.Resources.csproj")
ConvertTo-Json $SLNF | Out-File $TMP -Encoding UTF8
Move-Item -Path $TMP -Destination "typebeat.Desktop.slnf" -Force
