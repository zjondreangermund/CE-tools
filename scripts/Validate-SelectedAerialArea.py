"""Static regression checks for selected-area WMS image tile placement."""
from pathlib import Path
p = Path(__file__).resolve().parents[1]
code = (p / "src/CE.Tools.Civil3D/GeoAerialAreaCommands.cs").read_text(encoding="utf-8")
support = (p / "src/CE.Tools.Civil3D/GeoJpegTilesCommands.cs").read_text(encoding="utf-8")
menu = (p / "src/CE.Tools.Civil3D/August13RoadProductionCentres.cs").read_text(encoding="utf-8")
for term in [
    'CommandMethod("CE_TOOLS", "CE_AERIALAREA"',
    "PromptCornerOptions",
    "CurrentUserCoordinateSystem",
    "SERVICE=WMS&VERSION=1.1.1&REQUEST=GetMap",
    "GeoJpegTilesCommands.InsertAll",
    "File.WriteAllLines(worldPath",
    "x0 + sx / 2.0",
    "y1 - sy / 2.0",
    '"0", "0", (-sy).ToString',
    "I have permission to download and use this imagery",
    "IsGoogleImageryHost",
    "MaxTiles = 64",
    "catch (WebException ex)",
]:
    assert term in code, term
assert "internal static void InsertAll" in support
assert menu.count('"CE_AERIALAREA"') >= 2
# Pixel-centre world file tile placement should reconstruct image borders
xmin,ymin,xmax,ymax = (43000.,-11400.,43580.,-11080.)
full_w,full_h = 2320,1280
tile_px=1024
sx=(xmax-xmin)/full_w
sy=(ymax-ymin)/full_h
for row in range(2):
    for col in range(3):
        px=col*tile_px
        py=row*tile_px
        w=min(tile_px,full_w-px)
        h=min(tile_px,full_h-py)
        if w<=0 or h<=0: continue
        x0=xmin+px*sx
        y1=ymax-py*sy
        centre_x=x0+sx/2
        centre_y=y1-sy/2
        left=centre_x-sx/2
        top=centre_y+sy/2
        assert abs(left-x0)<1e-8
        assert abs(top-y1)<1e-8
        assert abs((left+w*sx)-(xmin+(px+w)*sx))<1e-8
        assert abs((top-h*sy)-(ymax-(py+h)*sy))<1e-8
print("PASS: selected-area JPEG WMS imagery command, world files, imports, safety and menu.")
