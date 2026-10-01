import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../artifacts/download-preview');
const types={'.html':'text/html; charset=utf-8','.css':'text/css; charset=utf-8','.svg':'image/svg+xml','.png':'image/png','.txt':'text/plain; charset=utf-8','.exe':'application/octet-stream'};
const server=http.createServer((req,res)=>{
  if(!['GET','HEAD'].includes(req.method)){res.writeHead(405);res.end();return;}
  let target;
  try {const url=new URL(req.url,'http://localhost');target=path.resolve(root,'.'+decodeURIComponent(url.pathname==='/'?'/index.html':url.pathname));}
  catch{res.writeHead(400);res.end();return;}
  if(!target.startsWith(root+path.sep)){res.writeHead(403);res.end();return;}
  let stat;try{stat=fs.statSync(target);}catch{res.writeHead(404);res.end();return;}
  if(!stat.isFile()){res.writeHead(404);res.end();return;}
  const headers={'Content-Type':types[path.extname(target)]||'application/octet-stream','Content-Length':stat.size,'X-Content-Type-Options':'nosniff','Referrer-Policy':'no-referrer','Cache-Control':'no-store'};
  if(path.extname(target)==='.exe')headers['Content-Disposition']=`attachment; filename="${path.basename(target)}"`;
  res.writeHead(200,headers);
  if(req.method==='HEAD')res.end();else fs.createReadStream(target).pipe(res);
});
server.listen(0,'127.0.0.1',()=>console.log(`Download preview: http://127.0.0.1:${server.address().port}/`));
