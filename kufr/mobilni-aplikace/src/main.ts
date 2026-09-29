import './style.css';

const app = document.querySelector<HTMLElement>('#app');

if (app === null) {
  throw new Error('Chybí kořenový prvek aplikace.');
}

app.innerHTML = `
  <h1>AI kufr</h1>
  <p>Mobilní ovládání připravujeme. Nejdříve zprovozníme mechaniku a elektroniku kufru.</p>
`;

