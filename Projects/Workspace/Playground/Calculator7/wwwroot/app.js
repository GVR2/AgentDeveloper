const a=document.getElementById('a'),b=document.getElementById('b'),op=document.getElementById('op'),out=document.getElementById('out');
document.getElementById('calc').onclick=()=>{const x=parseFloat(a.value),y=parseFloat(b.value);
if(Number.isNaN(x)||Number.isNaN(y)){out.textContent='Enter numbers';return;}
let r=0;switch(op.value){case '+':r=x+y;break;case '-':r=x-y;break;case '*':r=x*y;break;case '/':r=y===0?'∞':x/y;break;}out.textContent='Result: '+r;};