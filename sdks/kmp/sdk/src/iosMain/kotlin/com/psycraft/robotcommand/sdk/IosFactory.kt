package com.psycraft.robotcommand.sdk

public actual fun createRobotCommandLanClient(): RobotCommandLanClient = RobotCommandLanClient.forTesting(UnsupportedTransportFactory())
