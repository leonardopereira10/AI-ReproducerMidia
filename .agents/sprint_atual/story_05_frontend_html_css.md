# Story 05: Frontend — HTML + CSS (Mobile-First, Dark Theme)

## Descrição
Criar o layout HTML+CSS do painel web:
- Mobile-first (320px–1024px)
- Tema escuro (#121212 background, acentos azul/roxo)
- Header: título do episódio + nome da série
- Área de thumbnail
- Barra de progresso (seek) com tempo atual/total
- Botões de transporte: ⏮ | ⏪ | ▶/⏸ | ⏩ | ⏭
- Botão "Pular Abertura" (destaque visual)
- Slider de volume
- Label do dispositivo + perfil
- Seção de fila (próximos episódios)
- Estado de conexão (conectado/desconectado)

## Tipo
dev

## Critérios de Aceite
- [ ] Layout responsivo sem scroll horizontal em 320px+
- [ ] Tema escuro consistente
- [ ] Barra de progresso arrastável (touch-friendly, min 44px touch target)
- [ ] Botões com tamanho adequado para touch (min 44x44px)
- [ ] Botão "Pular Abertura" com destaque visual
- [ ] Seção de fila compacta (scroll horizontal ou lista)
- [ ] Indicador de conexão WebSocket (verde/vermelho)
- [ ] Arquivo index.html + style.css como EmbeddedResource
- [ ] Funciona em Chrome Android, Safari iOS, Chrome Desktop
