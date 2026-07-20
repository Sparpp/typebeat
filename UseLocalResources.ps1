$CSPROJ="typebeat.Game/typebeat.Game.csproj"
$SLN="typebeat.sln"

dotnet remove $CSPROJ package typebeat.Game.Resources;
dotnet sln $SLN add ../type-beat-assets/typebeat.Game.Resources/typebeat.Game.Resources.csproj
dotnet add $CSPROJ reference ../type-beat-assets/typebeat.Game.Resources/typebeat.Game.Resources.csproj

$SLNF=Get-Content "typebeat.Desktop.slnf" | ConvertFrom-Json
$TMP=New-TemporaryFile
$SLNF.solution.projects += ("../type-beat-assets/typebeat.Game.Resources/typebeat.Game.Resources.csproj")
ConvertTo-Json $SLNF | Out-File $TMP -Encoding UTF8
Move-Item -Path $TMP -Destination "typebeat.Desktop.slnf" -Force
