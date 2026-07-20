CSPROJ="typebeat.Game/typebeat.Game.csproj"
SLN="typebeat.sln"

dotnet remove $CSPROJ package typebeat.Game.Resources;
dotnet sln $SLN add ../type-beat-assets/typebeat.Game.Resources/typebeat.Game.Resources.csproj
dotnet add $CSPROJ reference ../type-beat-assets/typebeat.Game.Resources/typebeat.Game.Resources.csproj

SLNF="typebeat.Desktop.slnf"
TMP=$(mktemp)
jq '.solution.projects += ["../type-beat-assets/typebeat.Game.Resources/typebeat.Game.Resources.csproj"]' $SLNF > $TMP
mv -f $TMP $SLNF
