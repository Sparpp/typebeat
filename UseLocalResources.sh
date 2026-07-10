CSPROJ="typebeat.Game/typebeat.Game.csproj"
SLN="osu.sln"

dotnet remove $CSPROJ package ppy.typebeat.Game.Resources;
dotnet sln $SLN add ../osu-resources/typebeat.Game.Resources/typebeat.Game.Resources.csproj
dotnet add $CSPROJ reference ../osu-resources/typebeat.Game.Resources/typebeat.Game.Resources.csproj

SLNF="typebeat.Desktop.slnf"
TMP=$(mktemp)
jq '.solution.projects += ["../osu-resources/typebeat.Game.Resources/typebeat.Game.Resources.csproj"]' $SLNF > $TMP
mv -f $TMP $SLNF
