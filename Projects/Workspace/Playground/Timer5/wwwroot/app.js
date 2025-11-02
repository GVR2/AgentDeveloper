let h=0,m=0,s=0,t=null;const clock=document.getElementById('clock');
function render(){clock.textContent=[h,m,s].map(x=>String(x).padStart(2,'0')).join(':');}
document.getElementById('start').onclick=()=>{if(t)return;t=setInterval(()=>{s++;if(s==60){s=0;m++;}if(m==60){m=0;h++;}render();},1000);};
document.getElementById('stop').onclick=()=>{clearInterval(t);t=null;};
document.getElementById('reset').onclick=()=>{h=0;m=0;s=0;render();};render();