An RL Env for [Celeste](https://www.celestegame.com/)

The environment is provided as an Everest mod. An http server is started on level load to: control the player, restart an episode, retrieve observations, and retrieve reward signal. An episode tracks the player from spawn to death, each playable action is offered as action with an option to hold for a set number of frames, and reward is computed based on distance to room exit. 

Below is an example clip of a model trained to play the game (roughly) using this environment and in parallel. Training pipeline NOT open sourced


https://github.com/user-attachments/assets/31733cd7-374d-42bb-b4d8-85c8e9bab67f

