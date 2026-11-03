'use strict';
/** Fit the intrinsic video ratio while respecting native usable size; impossible ratios remain unchanged. */
function fitVideoSize(width,ratio,area) {
  if(!Number.isFinite(ratio) || ratio<=0 || !Number.isFinite(width) || !area || !Number.isFinite(area.width) || !Number.isFinite(area.height))return null;
  const minimum=Math.max(320,240*ratio),maximum=Math.min(area.width,area.height*ratio);
  if(minimum>maximum || maximum<=0)return null;
  const fitted=Math.max(minimum,Math.min(maximum,width));
  return {width:Math.round(fitted),height:Math.round(fitted/ratio)};
}
module.exports={fitVideoSize};
