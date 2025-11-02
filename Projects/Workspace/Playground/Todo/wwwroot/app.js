const input=document.getElementById('task'),list=document.getElementById('list');
document.getElementById('add').onclick=()=>{const v=input.value.trim();if(!v)return;const li=document.createElement('li');
li.textContent=v;const del=document.createElement('button');del.textContent='×';del.onclick=()=>li.remove();li.appendChild(del);list.appendChild(li);input.value='';};