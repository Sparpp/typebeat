CSPROJ="typebeat.Game/typebeat.Game.csproj"
SLN="typebeat.sln"

dotnet remove $CSPROJ package ppy.osu.Game.Resources;
dotnet sln $SLN add ../osu-resources/osu.Game.Resources/osu.Game.Resources.csproj
dotnet add $CSPROJ reference ../osu-resources/osu.Game.Resources/osu.Game.Resources.csproj

SLNF="typebeat.Desktop.slnf"
TMP=$(mktemp)
jq '.solution.projects += ["../osu-resources/osu.Game.Resources/osu.Game.Resources.csproj"]' $SLNF > $TMP
mv -f $TMP $SLNF
