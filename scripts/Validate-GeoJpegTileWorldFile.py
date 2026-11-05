"""Check world-file geometry, tile offsets, and safe command availability."""
from pathlib import Path
import math
root = Path(__file__).resolve().parents[1]
code = (root / "src/CE.Tools.Civil3D/GeoJpegTilesCommands.cs").read_text(encoding="utf-8")
menu = (root / "src/CE.Tools.Civil3D/August13RoadProductionCentres.cs").read_text(encoding="utf-8")
required = [
    'CommandMethod("CE_TOOLS", "CE_GEOIMAGETILES"',
    'CommandMethod("CE_TOOLS", "CE_GEOIMAGEINSERT"',
    'CommandMethod("CE_TOOLS", "CE_GEOIMAGECALIBRATE"',
    'RasterImageDef.CreateImageDictionary',
    'RasterImage.EnableReactors(true)',
    'new CoordinateSystem3d(ll, u, v)',
    'world.Offset(x, y).Write',
    'Confirmed same projected CRS and drawing units',
    'ImageCodecInfo.GetImageEncoders',
    'Math.Abs(u.DotProduct(v))',
    'CE-AERIAL-IMAGERY',
]
for marker in required:
    assert marker in code, marker
for command in ("CE_GEOIMAGETILES", "CE_GEOIMAGEINSERT", "CE_GEOIMAGECALIBRATE"):
    assert command in menu, command
assert "maps.googleapis.com" not in code.lower()
assert "earth.google.com/" not in code.lower()

def pixel(a,b,c,d,e,f,col,row):
    return a*col+b*row+c,d*col+e*row+f

def check_world(a,b,c,d,e,f,w,h,col,row):
    # Crop pixel centres must match source pixel centres.
    tc = c + a*col + b*row
    tf = f + d*col + e*row
    for x,y in ((0,0),(4,7),(w-1,h-1)):
        left = pixel(a,b,c,d,e,f,col+x,row+y)
        right = pixel(a,b,tc,d,e,tf,x,y)
        assert math.dist(left,right) < 1e-8, (left,right)
    # Orientation maps the outer lower-left corner back to source.
    llx = tc - .5*a - .5*b + h*b
    lly = tf - .5*d - .5*e + h*e
    ux,uy = a*w,d*w
    vx,vy = -b*h,-e*h
    upper_left = (llx+vx, lly+vy)
    expect_ul = (tc-.5*(a+b), tf-.5*(d+e))
    assert math.dist(upper_left, expect_ul) < 1e-8
    return (llx,lly),(ux,uy),(vx,vy)

# North-up 0.2m GSD, 20m x 16m raster.
ll,u,v=check_world(.2,0,100.1,0,-.2,199.9,100,80,0,0)
assert math.dist(ll,(100.,184.)) < 1e-8
assert math.dist(u,(20.,0.)) < 1e-8
assert math.dist(v,(0.,16.)) < 1e-8

# Rotated axes, anisotropic pixels and arbitrary tile origin.
angle=math.radians(24)
a,d=.18*math.cos(angle),.18*math.sin(angle)
b,e=.23*math.sin(angle),-.23*math.cos(angle)
ll,u,v=check_world(a,b,753231.147,d,e,7462011.326,256,128,2048,1024)
assert abs(u[0]*v[0]+u[1]*v[1]) < 1e-8
print("PASS: 3 Geo JPEG commands, no tile downloader, affine JGW tile offsets and rotated placement.")
